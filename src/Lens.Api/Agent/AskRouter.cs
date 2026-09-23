using System.Text.Json;
using System.Text.Json.Serialization;
using Lens.Core.Query;

namespace Lens.Api.Agent;

public sealed record AskRouterOptions
{
    /// <summary>auto: local planner, then Ollama, then Claude. Or force one of local | ollama | claude.</summary>
    public string Provider { get; init; } = "auto";
    /// <summary>JSONL file every question and its answer plan are appended to, for training your own model later. Null disables.</summary>
    public string? LogPath { get; init; }
}

public sealed record ProviderStatus(string Mode, bool Local, OllamaStatus Ollama, ClaudeStatus Claude);
public sealed record OllamaStatus(string Url, string Model, bool Available, string? Reason);
public sealed record ClaudeStatus(string Model, bool Configured);

/// <summary>
/// Decides who answers a question. The database-first planner handles the everyday shapes for free and instantly;
/// a local open model through Ollama takes the rest; Claude is only used when configured and nothing else could answer.
/// Every question and the plan that answered it is logged, so the log doubles as a training set for your own model.
/// </summary>
public sealed class AskRouter
{
    private readonly QuestionPlanner _planner;
    private readonly OllamaAgent _ollama;
    private readonly LensAgent _claude;
    private readonly AskRouterOptions _options;
    private readonly AgentOptions _claudeOptions;
    private readonly ILogger<AskRouter> _log;
    private readonly SemaphoreSlim _logGate = new(1, 1);

    private static readonly JsonSerializerOptions LogJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AskRouter(QuestionPlanner planner, OllamaAgent ollama, LensAgent claude, AskRouterOptions options, AgentOptions claudeOptions, ILogger<AskRouter> log)
    {
        _planner = planner;
        _ollama = ollama;
        _claude = claude;
        _options = options;
        _claudeOptions = claudeOptions;
        _log = log;
    }

    public static bool ClaudeConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));

    public async Task<ProviderStatus> StatusAsync(CancellationToken ct)
    {
        var (ok, reason) = await _ollama.ProbeAsync(ct);
        return new ProviderStatus(_options.Provider, true,
            new OllamaStatus(_ollama.Options.Url, _ollama.Options.Model, ok, reason),
            new ClaudeStatus(_claudeOptions.Model, ClaudeConfigured));
    }

    public async Task<AskResult> AskAsync(string question, int? videoId, CancellationToken ct)
    {
        var mode = _options.Provider.ToLowerInvariant();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        AskResult result;

        if (mode is "auto" or "local")
        {
            var local = await _planner.TryAnswerAsync(question, videoId, ct);
            if (local is not null)
            {
                var trace = local.Calls.Select(c => new ToolCallTrace(c.Tool, JsonSerializer.SerializeToElement(c.Input, LogJson), c.ResultRows, c.Ms)).ToList();
                result = new AskResult(local.Answer, local.Hits, trace, "local:" + local.Kind, 0, 0, "local", sw.Elapsed.TotalMilliseconds);
                await LogAsync(question, videoId, result, ct);
                return result;
            }
            if (mode == "local")
            {
                result = new AskResult(
                    "I could not work out what to search for. Try naming an object class and, if you like, a time: " +
                    "\"when was the first bus seen?\", \"how many trucks in the first 30 seconds?\", \"show me people after 6pm\".",
                    [], [], "unhandled", 0, 0, "local", sw.Elapsed.TotalMilliseconds);
                await LogAsync(question, videoId, result, ct);
                return result;
            }
        }

        if (mode is "auto" or "ollama")
        {
            var (available, reason) = await _ollama.ProbeAsync(ct);
            if (available)
            {
                try
                {
                    result = await _ollama.AskAsync(question, videoId, ct);
                    await LogAsync(question, videoId, result, ct);
                    return result;
                }
                catch (HttpRequestException ex) when (mode == "auto")
                {
                    _log.LogWarning(ex, "Ollama failed, falling through");
                }
            }
            else if (mode == "ollama")
            {
                throw new ProviderUnavailableException(reason ?? "Ollama is not available.");
            }
        }

        if (mode is "auto" or "claude")
        {
            if (ClaudeConfigured)
            {
                var r = await _claude.AskAsync(question, videoId, ct);
                result = r with { Ms = sw.Elapsed.TotalMilliseconds };
                await LogAsync(question, videoId, result, ct);
                return result;
            }
            if (mode == "claude")
                throw new ProviderUnavailableException("No Claude API key is configured. Set ANTHROPIC_API_KEY and restart the API.");
        }

        var (_, why) = await _ollama.ProbeAsync(ct);
        throw new ProviderUnavailableException(
            "That question needs a language model and none is available. " + (why ?? "") +
            " Simple questions (\"when was the first bus?\", \"how many cars?\", \"show me people after 6pm\") are answered without a model.");
    }

    private async Task LogAsync(string question, int? videoId, AskResult result, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.LogPath)) return;
        var entry = new
        {
            at = DateTimeOffset.Now,
            question,
            videoId,
            result.Provider,
            result.StopReason,
            toolCalls = result.ToolCalls.Select(t => new { t.Tool, t.Input }),
            result.Answer,
            hits = result.Hits.Count,
            ms = Math.Round(result.Ms),
        };
        var line = JsonSerializer.Serialize(entry, LogJson) + "\n";
        await _logGate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.LogPath)!);
            await File.AppendAllTextAsync(_options.LogPath, line, ct);
        }
        catch (IOException ex) { _log.LogWarning(ex, "could not write ask log"); }
        finally { _logGate.Release(); }
    }
}

public sealed class ProviderUnavailableException(string message) : Exception(message);
