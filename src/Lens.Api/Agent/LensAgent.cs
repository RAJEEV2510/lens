using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Lens.Core.Storage;

namespace Lens.Api.Agent;

public sealed record AgentOptions
{
    public string Model { get; init; } = "claude-opus-5";
    public int MaxToolRounds { get; init; } = 8;
}

/// <summary>
/// Turns a plain-English question into store queries via Claude tool use, then into a short answer.
/// A manual tool loop keeps every call visible: the trace goes back to the UI so a reviewer can see how the answer was reached.
/// </summary>
public sealed class LensAgent
{
    private readonly AnthropicClient _client;
    private readonly AgentTools _tools;
    private readonly AgentOptions _options;
    private readonly ILogger<LensAgent> _log;

    public LensAgent(AnthropicClient client, AgentTools tools, AgentOptions options, ILogger<LensAgent> log)
    {
        _client = client;
        _tools = tools;
        _options = options;
        _log = log;
    }

    private const string SystemPrompt = """
        You are Lens, an assistant that answers questions about indexed video footage.

        How the data works:
        - Footage was sampled at 2 frames per second and every frame was run through an object detector (COCO classes: person, car, truck, bus, motorcycle, bicycle, and so on), plus a face detector whose boxes are class "face".
        - Every detection has a time in seconds from the start of its video and a wall-clock time. Use wall-clock when the user talks about clock times ("after 6pm"), seconds when they talk about video positions ("around the 30 second mark").
        - The detector does not know colours, licence plates, who a face belongs to, or directions of travel. If asked, say plainly that this is not available yet rather than guessing.
        - Counts are sightings per sampled frame, not unique objects. To estimate distinct objects or events, use search_detections and count the merged events. Say "about" when estimating.
        - A note on classes: three-wheeled auto-rickshaws are often detected as "truck" or "motorcycle". Mention this if a truck count looks surprising for Indian road footage.

        How to answer:
        - Call list_videos first if you do not yet know which videos or cameras exist.
        - Prefer one or two well-chosen tool calls over many.
        - Answer in a few short sentences. Give times as m:ss into the video plus the clock time when useful. Mention which video or camera.
        - When you list events, keep it to the most relevant ten or fewer.
        - If the tool results do not support an answer, say what you looked for and that nothing matched.
        """;

    public async Task<AskResult> AskAsync(string question, int? videoId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var context = $"Current date/time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}.";
        if (videoId is { } vid) context += $" The user is currently looking at video_id {vid}; prefer it unless they ask about others.";

        List<MessageParam> messages =
        [
            new() { Role = Role.User, Content = $"{context}\n\nQuestion: {question}" },
        ];

        var hits = new List<DetectionHit>();
        var trace = new List<ToolCallTrace>();
        long inTok = 0, outTok = 0;
        Message? response = null;

        for (var round = 0; round <= _options.MaxToolRounds; round++)
        {
            response = await _client.Messages.Create(new MessageCreateParams
            {
                Model = _options.Model,
                MaxTokens = 4096,
                System = new List<TextBlockParam> { new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() } },
                Tools = AgentTools.Definitions.Select(t => (ToolUnion)t).ToList(),
                Messages = messages,
            }, cancellationToken: ct);

            inTok += response.Usage.InputTokens;
            outTok += response.Usage.OutputTokens;

            var stop = response.StopReason?.ToString() ?? "";
            if (stop != "tool_use") break;

            // Echo the assistant turn back, then run every requested tool and return all results in one user turn.
            List<ContentBlockParam> assistantContent = [];
            List<ContentBlockParam> toolResults = [];
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? text))
                {
                    assistantContent.Add(new TextBlockParam { Text = text.Text });
                }
                else if (block.TryPickThinking(out ThinkingBlock? thinking))
                {
                    assistantContent.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                {
                    assistantContent.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? toolUse))
                {
                    assistantContent.Add(new ToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });

                    var started = System.Diagnostics.Stopwatch.StartNew();
                    string resultJson;
                    var isError = false;
                    try
                    {
                        var (json, found) = await _tools.ExecuteAsync(toolUse.Name, toolUse.Input, ct);
                        resultJson = json;
                        hits.AddRange(found);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "tool {Tool} failed", toolUse.Name);
                        resultJson = JsonSerializer.Serialize(new { error = ex.Message });
                        isError = true;
                    }
                    trace.Add(new ToolCallTrace(toolUse.Name, JsonSerializer.SerializeToElement(toolUse.Input), resultJson.Length, started.Elapsed.TotalMilliseconds));
                    toolResults.Add(new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = resultJson, IsError = isError });
                }
            }

            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
            messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
        }

        var answer = string.Join("\n", response!.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        var stopReason = response.StopReason?.ToString() ?? "unknown";
        if (stopReason == "refusal") answer = "I can't help with that question.";
        if (stopReason == "max_tokens" && answer.Length == 0) answer = "The answer was cut off. Try a narrower question.";

        // De-duplicate hits across tool calls, keep chronological order.
        var unique = hits.GroupBy(h => (h.VideoId, h.ClassName, h.TimestampSeconds)).Select(g => g.First()).OrderBy(h => h.OccurredAt).ToList();
        return new AskResult(answer, unique, trace, stopReason, inTok, outTok, "claude:" + _options.Model, sw.Elapsed.TotalMilliseconds);
    }
}
