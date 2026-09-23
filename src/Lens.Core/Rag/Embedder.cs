using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Lens.Core.Rag;

/// <summary>Turns text into vectors. One implementation talks to Ollama; tests use a fake.</summary>
public interface IEmbedder
{
    string Model { get; }
    Task<(bool Available, string? Reason)> ProbeAsync(CancellationToken ct = default);
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// <summary>Embeddings from a local open model through Ollama's /api/embed. Nothing leaves the machine.</summary>
public sealed class OllamaEmbedder : IEmbedder
{
    private readonly HttpClient _http;
    private (DateTimeOffset At, bool Ok, string? Reason) _probe = (DateTimeOffset.MinValue, false, null);

    public OllamaEmbedder(HttpClient http, string url, string model)
    {
        _http = http;
        _http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(10);
        Model = model;
    }

    public string Model { get; }

    public async Task<(bool Available, string? Reason)> ProbeAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _probe.At < TimeSpan.FromSeconds(30)) return (_probe.Ok, _probe.Reason);
        bool ok; string? reason = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            var tags = await _http.GetFromJsonAsync<JsonNode>("api/tags", cts.Token);
            var names = tags?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>() ?? "").ToList() ?? [];
            ok = names.Any(n => n == Model || n == Model + ":latest" || (!Model.Contains(':') && n.StartsWith(Model + ":")));
            if (!ok) reason = $"Embedding model {Model} is not pulled. Run: ollama pull {Model}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            ok = false;
            reason = $"Ollama is not reachable at {_http.BaseAddress}. Install it from https://ollama.com and run: ollama pull {Model}";
        }
        _probe = (DateTimeOffset.UtcNow, ok, reason);
        return (ok, reason);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        var body = new JsonObject
        {
            ["model"] = Model,
            ["input"] = new JsonArray(texts.Select(t => (JsonNode)t).ToArray()),
            ["keep_alive"] = "30m",
        };
        using var response = await _http.PostAsJsonAsync("api/embed", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Ollama embed returned {(int)response.StatusCode}: {text}");
        var node = JsonNode.Parse(text)!;
        var rows = node["embeddings"]!.AsArray();
        var result = new float[rows.Count][];
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]!.AsArray();
            var v = new float[row.Count];
            for (var j = 0; j < row.Count; j++) v[j] = (float)row[j]!.GetValue<double>();
            result[i] = Normalise(v);
        }
        return result;
    }

    /// <summary>Unit length, so a dot product is the cosine similarity.</summary>
    public static float[] Normalise(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += x * x;
        var norm = (float)Math.Sqrt(sum);
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }
}
