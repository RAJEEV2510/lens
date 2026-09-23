using Lens.Core.Models;
using Lens.Core.Rag;
using Lens.Core.Storage;

namespace Lens.Tests;

/// <summary>The optional RAG path: event sentences, the file vector index, and incremental index builds.</summary>
public class RagTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lens-rag-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 18, 0, 0, TimeSpan.FromHours(5.5));

    public RagTests() => Directory.CreateDirectory(_dir);

    /// <summary>Deterministic stand-in for the embedding model: a bag of characters, unit length. Similar text gives similar vectors.</summary>
    private sealed class FakeEmbedder : IEmbedder
    {
        public string Model => "fake";
        public int Calls;
        public Task<(bool, string?)> ProbeAsync(CancellationToken ct = default) => Task.FromResult<(bool, string?)>((true, null));
        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(texts.Select(t =>
            {
                var v = new float[64];
                foreach (var ch in t.ToLowerInvariant()) v[ch % 64] += 1;
                return OllamaEmbedder.Normalise(v);
            }).ToArray());
        }
    }

    private async Task<(JsonFileStore store, VideoInfo video)> SeedAsync()
    {
        var store = new JsonFileStore(Path.Combine(_dir, "store.json"));
        var id = await store.UpsertVideoAsync(new VideoInfo
        {
            Path = @"C:\clips\junction.mp4", Name = "junction.mp4", Camera = "gate-2",
            Width = 1600, Height = 1200, Fps = 12, DurationSeconds = 60, StartedAt = Start,
        });
        var dets = new List<Detection>();
        for (var i = 0; i < 5; i++) dets.Add(new Detection(id, 20 + i, 10 + i * 0.5, 2, "car", 0.7f, 0.1f, 0.1f, 0.5f, 0.5f));
        dets.Add(new Detection(id, 60, 30, 2, "car", 0.9f, 0.7f, 0.7f, 0.8f, 0.8f));
        dets.Add(new Detection(id, 22, 11, 0, "person", 0.8f, 0.45f, 0.45f, 0.55f, 0.6f));
        await store.WriteDetectionsAsync(id, ToAsync(dets));
        return (store, (await store.GetVideoAsync(id))!);
    }

    [Fact]
    public async Task Event_sentence_describes_class_camera_time_size_and_position()
    {
        var (store, video) = await SeedAsync();
        var hits = await store.SearchAsync(new SearchRequest { Filter = new DetectionFilter { Classes = ["car"] } });
        var doc = RagIndexer.ToDocument(video, hits[0]);
        Assert.Equal("1:car:10.0", doc.Id);
        Assert.Contains("A large car on camera gate-2 (junction.mp4)", doc.Text);
        Assert.Contains("at 0:10 into the video, 18:00:10", doc.Text);
        Assert.Contains("in view for 2 seconds (5 sightings", doc.Text);
        Assert.Contains("top left of the frame", doc.Text);
        Assert.Equal(12, doc.ToHit().EndSeconds);
    }

    [Fact]
    public async Task Index_persists_to_disk_and_ranks_by_similarity()
    {
        var basePath = Path.Combine(_dir, "vectors");
        var embedder = new FakeEmbedder();
        var docs = new[] { Doc("a", "a large car on camera gate-2"), Doc("b", "a small person on camera gate-2"), Doc("c", "a small dog on camera yard") };
        var index = new FileVectorIndex(basePath);
        await index.AddAsync(docs, await embedder.EmbedAsync(docs.Select(d => d.Text).ToList()));
        Assert.Equal(3, index.Count);

        var reopened = new FileVectorIndex(basePath);
        Assert.Equal(3, reopened.Count);
        Assert.Equal(64, reopened.Dimension);
        var q = (await embedder.EmbedAsync(["a small dog on camera yard"]))[0];
        var top = reopened.Search(q, 2);
        Assert.Equal("c", top[0].Document.Id);
        Assert.True(top[0].Score > top[1].Score);

        var filtered = reopened.Search(q, 5, d => d.VideoId == 2);
        Assert.All(filtered, m => Assert.Equal(2, m.Document.VideoId));
    }

    [Fact]
    public async Task Build_is_incremental()
    {
        var (store, _) = await SeedAsync();
        var embedder = new FakeEmbedder();
        var index = new FileVectorIndex(Path.Combine(_dir, "vectors"));
        var indexer = new RagIndexer(store, embedder, index);

        await indexer.BuildAsync(rebuild: false, CancellationToken.None);
        Assert.Equal(3, index.Count);                      // two car events + one person event
        Assert.Equal(1, embedder.Calls);
        Assert.False(indexer.Progress.Running);
        Assert.Equal(1, indexer.Progress.VideosDone);

        await indexer.BuildAsync(rebuild: false, CancellationToken.None);
        Assert.Equal(3, index.Count);
        Assert.Equal(1, embedder.Calls);                   // nothing new, nothing embedded

        await indexer.BuildAsync(rebuild: true, CancellationToken.None);
        Assert.Equal(3, index.Count);
        Assert.Equal(2, embedder.Calls);
    }

    private static EventDocument Doc(string id, string text) => new()
    {
        Id = id, Text = text, VideoId = id == "c" ? 2 : 1, VideoName = "v", Camera = "gate-2", ClassName = "car",
        StartSeconds = 0, EndSeconds = 1, BestSeconds = 0, OccurredAt = Start, Count = 1, Confidence = 0.5f,
        X1 = 0, Y1 = 0, X2 = 0.1f, Y2 = 0.1f,
    };

    private static async IAsyncEnumerable<Detection> ToAsync(IEnumerable<Detection> items)
    {
        foreach (var d in items) { yield return d; await Task.Yield(); }
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}
