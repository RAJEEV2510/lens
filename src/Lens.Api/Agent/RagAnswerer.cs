using System.Text;
using System.Text.Json;
using Lens.Core.Rag;

namespace Lens.Api.Agent;

public sealed record RagStatus(string EmbedModel, bool Available, string? Reason, int Indexed, RagIndexProgress Progress);

/// <summary>
/// The optional retrieval-augmented path. The question is embedded, the closest event descriptions are pulled from
/// the vector index, and the local model writes an answer from those events only. Good for fuzzy "anything unusual
/// near the gate" questions; for exact counts and time windows the database-first path is more accurate, which is
/// why this is a switch and not the default.
/// </summary>
public sealed class RagAnswerer
{
    private readonly RagIndexer _indexer;
    private readonly OllamaAgent _chat;
    private readonly ILogger<RagAnswerer> _log;

    public RagAnswerer(RagIndexer indexer, OllamaAgent chat, ILogger<RagAnswerer> log)
    {
        _indexer = indexer;
        _chat = chat;
        _log = log;
    }

    public async Task<RagStatus> StatusAsync(CancellationToken ct)
    {
        var (ok, reason) = await _indexer.Embedder.ProbeAsync(ct);
        return new RagStatus(_indexer.Embedder.Model, ok, reason, _indexer.Index.Count, _indexer.Progress);
    }

    private const string SystemPrompt = """
        You are Lens, answering a question about CCTV footage from a list of retrieved detection events.
        The events were retrieved by similarity to the question, so they are the closest matches, not a complete list:
        do not present counts as totals, say "among the retrieved events". Answer only from the events given; if they do
        not contain the answer, say so plainly. Two to four short sentences. Give times as m:ss into the video and the clock time.
        """;

    public async Task<AskResult> AskAsync(string question, int? videoId, int k, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var trace = new List<ToolCallTrace>();

        var (ok, reason) = await _indexer.Embedder.ProbeAsync(ct);
        if (!ok) throw new ProviderUnavailableException(reason ?? "The embedding model is not available.");
        if (_indexer.Index.Count == 0)
            throw new ProviderUnavailableException("The RAG index is empty. Click \"Build index\" on the Ask page (or POST /api/rag/index) and try again.");

        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var query = (await _indexer.Embedder.EmbedAsync([question], ct))[0];
        trace.Add(new ToolCallTrace("embed_question", JsonSerializer.SerializeToElement(new { model = _indexer.Embedder.Model }), query.Length, t0.Elapsed.TotalMilliseconds));

        t0.Restart();
        var matches = _indexer.Index.Search(query, k, videoId is { } v ? d => d.VideoId == v : null);
        trace.Add(new ToolCallTrace("vector_search", JsonSerializer.SerializeToElement(new { k, video_id = videoId, indexed = _indexer.Index.Count }), matches.Count, t0.Elapsed.TotalMilliseconds));

        var hits = matches.Select(m => m.Document.ToHit()).ToList();
        var context = new StringBuilder("Retrieved events, closest first:\n");
        for (var i = 0; i < matches.Count; i++)
            context.Append($"{i + 1}. [{matches[i].Score:0.00}] {matches[i].Document.Text}\n");

        var (chatOk, _) = await _chat.ProbeAsync(ct);
        if (!chatOk)
        {
            // No generator: return the retrieval itself, which is still useful.
            var listing = string.Join("\n", matches.Take(8).Select((m, i) => $"{i + 1}. {m.Document.Text}"));
            return new AskResult($"No chat model is available to write an answer, so here are the closest events to your question:\n{listing}",
                hits, trace, "retrieval_only", 0, 0, "rag:" + _indexer.Embedder.Model, sw.Elapsed.TotalMilliseconds);
        }

        t0.Restart();
        var (answer, inTok, outTok, stop) = await _chat.ChatAsync(SystemPrompt, context + "\nQuestion: " + question, ct);
        trace.Add(new ToolCallTrace("generate", JsonSerializer.SerializeToElement(new { model = _chat.Options.Model, context_events = matches.Count }), answer.Length, t0.Elapsed.TotalMilliseconds));
        if (answer.Length == 0) answer = "The model returned no answer. The closest events are shown below.";

        return new AskResult(answer, hits, trace, stop, inTok, outTok, $"rag:{_indexer.Embedder.Model}+{_chat.Options.Model}", sw.Elapsed.TotalMilliseconds);
    }
}
