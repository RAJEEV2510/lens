using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lens.Core.Storage;

namespace Lens.Api.Agent;

public sealed record OllamaOptions
{
    public string Url { get; init; } = "http://localhost:11434";
    public string Model { get; init; } = "qwen2.5:3b";
    public int MaxToolRounds { get; init; } = 6;
    public int ContextTokens { get; init; } = 8192;
}

/// <summary>
/// The same tool loop as <see cref="LensAgent"/>, but against a locally running open model through Ollama's chat API.
/// Nothing leaves the machine and there is no per-question cost. Small models get the footage inventory up front so
/// they can usually answer with a single search or count call.
/// </summary>
public sealed class OllamaAgent
{
    private readonly HttpClient _http;
    private readonly AgentTools _tools;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaAgent> _log;
    private (DateTimeOffset At, bool Available, string? Reason) _probe = (DateTimeOffset.MinValue, false, null);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public OllamaAgent(HttpClient http, AgentTools tools, OllamaOptions options, ILogger<OllamaAgent> log)
    {
        _http = http;
        _http.BaseAddress = new Uri(options.Url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(10); // CPU inference on a laptop can take a while
        _tools = tools;
        _options = options;
        _log = log;
    }

    public OllamaOptions Options => _options;

    /// <summary>True when Ollama answers and the configured model is pulled. Cached for 30 seconds.</summary>
    public async Task<(bool Available, string? Reason)> ProbeAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _probe.At < TimeSpan.FromSeconds(30)) return (_probe.Available, _probe.Reason);
        bool ok; string? reason = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var tags = await _http.GetFromJsonAsync<JsonNode>("api/tags", cts.Token);
            var names = tags?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").ToList() ?? [];
            ok = names.Any(n => n == _options.Model || n == _options.Model + ":latest" || (!_options.Model.Contains(':') && n.StartsWith(_options.Model + ":")));
            if (!ok) reason = names.Count == 0
                ? $"Ollama is running but no models are pulled. Run: ollama pull {_options.Model}"
                : $"Model {_options.Model} is not pulled (have: {string.Join(", ", names)}). Run: ollama pull {_options.Model}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            ok = false;
            reason = $"Ollama is not reachable at {_options.Url}. Install it from https://ollama.com and run: ollama pull {_options.Model}";
        }
        _probe = (DateTimeOffset.UtcNow, ok, reason);
        return (ok, reason);
    }

    private const string SystemPrompt = """
        You are Lens, an assistant that answers questions about indexed CCTV footage using tools.

        Facts about the data:
        - Footage was sampled at 2 frames per second and every frame went through an object detector with COCO classes (person, car, truck, bus, motorcycle, bicycle, ...).
        - Every detection has seconds into its video and a wall-clock time. Use from_time/to_time for clock times like "after 6pm", from_seconds/to_seconds for video positions like "first 30 seconds".
        - The detector does not know colours, licence plates, faces, speed or direction. Say plainly that this is not available; never guess.
        - Counts are sightings per sampled frame, not unique objects. For "how many" prefer search_detections and report the number of events as an estimate.
        - Auto-rickshaws are usually detected as "truck" or "motorcycle".

        How to answer:
        - The indexed footage is listed below, so you do not need list_videos unless the list is empty.
        - Make one tool call, usually search_detections with the right classes and video_id, then answer.
        - Answer in two or three short sentences. Give times as m:ss into the video and the clock time. Name the video or camera.
        - If nothing matched, say what you looked for.
        """;

    public async Task<AskResult> AskAsync(string question, int? videoId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var trace = new List<ToolCallTrace>();
        var hits = new List<DetectionHit>();

        // Prefetch the inventory: one fewer round trip, and small models plan much better when they can see the video ids.
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var (inventory, _) = await _tools.ExecuteAsync("list_videos", new Dictionary<string, JsonElement>(), ct);
        trace.Add(new ToolCallTrace("list_videos", JsonSerializer.SerializeToElement(new { prefetched = true }), inventory.Length, t0.Elapsed.TotalMilliseconds));

        var context = $"Current date/time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}.";
        if (videoId is { } vid) context += $" The user is looking at video_id {vid}; prefer it unless they ask about others.";

        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt + "\n\nIndexed footage (JSON):\n" + inventory },
            new JsonObject { ["role"] = "user", ["content"] = $"{context}\n\nQuestion: {question}" },
        };
        var tools = new JsonArray(AgentTools.Specs.Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = JsonNode.Parse(t.ParametersSchema.GetRawText()),
            },
        }).ToArray());

        long inTok = 0, outTok = 0;
        var answer = "";
        var stop = "end_turn";

        for (var round = 0; round <= _options.MaxToolRounds; round++)
        {
            var body = new JsonObject
            {
                ["model"] = _options.Model,
                ["stream"] = false,
                ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = _options.ContextTokens },
                ["messages"] = messages.DeepClone(),
                ["tools"] = tools.DeepClone(),
            };
            using var response = await _http.PostAsJsonAsync("api/chat", body, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Ollama returned {(int)response.StatusCode}: {text}");

            var reply = JsonNode.Parse(text)!;
            inTok += reply["prompt_eval_count"]?.GetValue<long>() ?? 0;
            outTok += reply["eval_count"]?.GetValue<long>() ?? 0;
            var message = reply["message"]?.AsObject() ?? new JsonObject();
            var content = message["content"]?.GetValue<string>() ?? "";
            var toolCalls = message["tool_calls"]?.AsArray();

            // Some small models write the call as text instead of using the tool channel. Rescue that.
            if ((toolCalls is null || toolCalls.Count == 0) && TryParseInlineToolCall(content) is { } inline)
            {
                toolCalls = new JsonArray(inline);
                content = "";
            }

            if (toolCalls is null || toolCalls.Count == 0)
            {
                answer = content.Trim();
                stop = reply["done_reason"]?.GetValue<string>() is "length" ? "max_tokens" : "end_turn";
                break;
            }

            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content, ["tool_calls"] = toolCalls.DeepClone() });
            foreach (var call in toolCalls)
            {
                var fn = call?["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                var args = ParseArguments(fn?["arguments"]);
                var started = System.Diagnostics.Stopwatch.StartNew();
                string resultJson;
                try
                {
                    var (json, found) = await _tools.ExecuteAsync(name, args, ct);
                    resultJson = json;
                    hits.AddRange(found);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "tool {Tool} failed", name);
                    resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                }
                trace.Add(new ToolCallTrace(name, JsonSerializer.SerializeToElement(args), resultJson.Length, started.Elapsed.TotalMilliseconds));
                messages.Add(new JsonObject { ["role"] = "tool", ["content"] = resultJson, ["tool_name"] = name });
            }
            if (round == _options.MaxToolRounds) stop = "max_rounds";
        }

        if (answer.Length == 0 && hits.Count > 0)
            answer = $"Found {hits.Count} matching events; see the frames below.";
        if (answer.Length == 0)
            answer = "The local model returned no answer. Try rephrasing, or ask a simpler question such as \"when was the first bus seen?\".";

        var unique = hits.GroupBy(h => (h.VideoId, h.ClassName, h.TimestampSeconds)).Select(g => g.First()).OrderBy(h => h.OccurredAt).ToList();
        return new AskResult(answer, unique, trace, stop, inTok, outTok, "ollama:" + _options.Model, sw.Elapsed.TotalMilliseconds);
    }

    private static Dictionary<string, JsonElement> ParseArguments(JsonNode? node)
    {
        var dict = new Dictionary<string, JsonElement>();
        if (node is null) return dict;
        // Arguments arrive as an object, or occasionally as a JSON string holding one.
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { node = JsonNode.Parse(s); } catch (JsonException) { return dict; }
        }
        if (node is JsonObject obj)
            foreach (var (k, val) in obj)
                dict[k] = val is null ? default : JsonSerializer.SerializeToElement(val);
        return dict;
    }

    private static JsonObject? TryParseInlineToolCall(string content)
    {
        var s = content.Trim();
        if (s.StartsWith("```")) s = s.Trim('`').Replace("json", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!s.StartsWith('{')) return null;
        try
        {
            var node = JsonNode.Parse(s) as JsonObject;
            var name = node?["name"]?.GetValue<string>();
            if (name is null || AgentTools.Specs.All(t => t.Name != name)) return null;
            var args = node!["arguments"] ?? node["parameters"] ?? new JsonObject();
            return new JsonObject { ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() } };
        }
        catch (JsonException) { return null; }
    }
}
