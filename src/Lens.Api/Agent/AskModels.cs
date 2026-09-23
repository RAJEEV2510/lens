using System.Text.Json;
using Lens.Core.Storage;

namespace Lens.Api.Agent;

public sealed record ToolCallTrace(string Tool, JsonElement Input, int ResultChars, double Ms);

/// <summary>What the Ask box gets back, whoever produced it: the local planner, a local open model, or Claude.</summary>
public sealed record AskResult(
    string Answer,
    IReadOnlyList<DetectionHit> Hits,
    IReadOnlyList<ToolCallTrace> ToolCalls,
    string StopReason,
    long InputTokens,
    long OutputTokens,
    /// <summary>"local", "ollama:&lt;model&gt;" or "claude:&lt;model&gt;".</summary>
    string Provider,
    double Ms);
