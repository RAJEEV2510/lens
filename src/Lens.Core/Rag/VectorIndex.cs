using System.Text.Json;
using System.Text.Json.Serialization;
using Lens.Core.Storage;

namespace Lens.Core.Rag;

/// <summary>One detection event written as a sentence, plus the fields needed to show its frame again.</summary>
public sealed record EventDocument
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public required int VideoId { get; init; }
    public required string VideoName { get; init; }
    public required string Camera { get; init; }
    public required string ClassName { get; init; }
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
    public required double BestSeconds { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required int Count { get; init; }
    public required float Confidence { get; init; }
    public required float X1 { get; init; }
    public required float Y1 { get; init; }
    public required float X2 { get; init; }
    public required float Y2 { get; init; }

    public DetectionHit ToHit() => new()
    {
        VideoId = VideoId, VideoName = VideoName, Camera = Camera, ClassName = ClassName,
        TimestampSeconds = StartSeconds, EndSeconds = EndSeconds, BestSeconds = BestSeconds, OccurredAt = OccurredAt,
        Count = Count, Confidence = Confidence, X1 = X1, Y1 = Y1, X2 = X2, Y2 = Y2,
    };
}

public sealed record VectorMatch(EventDocument Document, float Score);

/// <summary>
/// A small on-disk vector index: documents as JSON lines, vectors as a flat float32 file in the same order, all held in
/// memory and searched by brute-force cosine. Fine up to a few hundred thousand events; past that, the same interface
/// belongs on pgvector.
/// </summary>
public sealed class FileVectorIndex
{
    private readonly string _docsPath;
    private readonly string _vecPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<EventDocument> _docs = [];
    private readonly List<float[]> _vectors = [];
    private readonly HashSet<string> _ids = [];
    private bool _loaded;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public FileVectorIndex(string basePath)
    {
        _docsPath = basePath + ".jsonl";
        _vecPath = basePath + ".f32";
    }

    public int Dimension { get; private set; }
    public int Count { get { EnsureLoaded(); return _docs.Count; } }

    public bool Contains(string id) { EnsureLoaded(); return _ids.Contains(id); }

    public async Task AddAsync(IReadOnlyList<EventDocument> docs, IReadOnlyList<float[]> vectors, CancellationToken ct = default)
    {
        if (docs.Count != vectors.Count) throw new ArgumentException("one vector per document");
        if (docs.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            if (Dimension == 0) Dimension = vectors[0].Length;
            Directory.CreateDirectory(Path.GetDirectoryName(_docsPath)!);
            await using var docStream = new FileStream(_docsPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var docWriter = new StreamWriter(docStream);
            await using var vecStream = new FileStream(_vecPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            var buffer = new byte[Dimension * sizeof(float)];
            for (var i = 0; i < docs.Count; i++)
            {
                if (_ids.Contains(docs[i].Id)) continue;
                if (vectors[i].Length != Dimension) throw new ArgumentException($"vector dimension {vectors[i].Length} != {Dimension}");
                await docWriter.WriteLineAsync(JsonSerializer.Serialize(docs[i], Json));
                Buffer.BlockCopy(vectors[i], 0, buffer, 0, buffer.Length);
                await vecStream.WriteAsync(buffer, ct);
                _docs.Add(docs[i]);
                _vectors.Add(vectors[i]);
                _ids.Add(docs[i].Id);
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Top-k by cosine similarity. The optional filter narrows by video before ranking, which is how real systems combine both.</summary>
    public IReadOnlyList<VectorMatch> Search(float[] query, int k, Func<EventDocument, bool>? filter = null)
    {
        EnsureLoaded();
        if (_docs.Count == 0 || query.Length != Dimension) return [];
        var top = new List<VectorMatch>(k + 1);
        for (var i = 0; i < _docs.Count; i++)
        {
            if (filter is not null && !filter(_docs[i])) continue;
            var v = _vectors[i];
            float dot = 0;
            for (var j = 0; j < v.Length; j++) dot += v[j] * query[j];
            if (top.Count < k || dot > top[^1].Score)
            {
                top.Add(new VectorMatch(_docs[i], dot));
                top.Sort((a, b) => b.Score.CompareTo(a.Score));
                if (top.Count > k) top.RemoveAt(top.Count - 1);
            }
        }
        return top;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _docs.Clear(); _vectors.Clear(); _ids.Clear(); Dimension = 0; _loaded = true;
            if (File.Exists(_docsPath)) File.Delete(_docsPath);
            if (File.Exists(_vecPath)) File.Delete(_vecPath);
        }
        finally { _gate.Release(); }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(_docsPath) || !File.Exists(_vecPath)) return;
        var lines = File.ReadAllLines(_docsPath).Where(l => l.Length > 0).ToList();
        var bytes = File.ReadAllBytes(_vecPath);
        if (lines.Count == 0) return;
        Dimension = bytes.Length / sizeof(float) / lines.Count;
        var stride = Dimension * sizeof(float);
        for (var i = 0; i < lines.Count; i++)
        {
            var doc = JsonSerializer.Deserialize<EventDocument>(lines[i], Json);
            if (doc is null || (i + 1) * stride > bytes.Length) break;
            var v = new float[Dimension];
            Buffer.BlockCopy(bytes, i * stride, v, 0, stride);
            _docs.Add(doc); _vectors.Add(v); _ids.Add(doc.Id);
        }
    }
}
