using System.Text.Json;
using System.Text.Json.Serialization;
using Lens.Core.Models;

namespace Lens.Core.Storage;

/// <summary>
/// A no-database store backed by one JSON file. Lets the indexer and the API run on a laptop with nothing installed,
/// and doubles as the fixture format for the eval suite. Not for large libraries: everything lives in memory.
/// </summary>
public sealed class JsonFileStore : IDetectionStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Db _db = new();
    private bool _loaded;

    public JsonFileStore(string path) => _path = path;

    public async Task EnsureSchemaAsync(CancellationToken ct = default) => await LoadAsync(ct);

    public async Task<int> UpsertVideoAsync(VideoInfo video, bool clearDetections = true, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var existing = _db.Videos.FirstOrDefault(v => string.Equals(v.Path, video.Path, StringComparison.OrdinalIgnoreCase));
            var id = existing?.Id ?? (_db.Videos.Count == 0 ? 1 : _db.Videos.Max(v => v.Id) + 1);
            if (existing is not null) _db.Videos.Remove(existing);
            _db.Videos.Add(video with { Id = id, IndexedAt = DateTimeOffset.UtcNow });
            if (clearDetections) _db.Detections.RemoveAll(d => d.VideoId == id);
            await SaveAsync(ct);
            return id;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateVideoDurationAsync(int id, double durationSeconds, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var i = _db.Videos.FindIndex(v => v.Id == id);
            if (i < 0) return;
            if (durationSeconds > _db.Videos[i].DurationSeconds)
            {
                _db.Videos[i] = _db.Videos[i] with { DurationSeconds = durationSeconds };
                await SaveAsync(ct);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<VideoSource>> ListSourcesAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        return _db.Sources.OrderBy(s => s.Id).ToList();
    }

    public async Task<int> SaveSourceAsync(VideoSource source, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var id = source.Id > 0 ? source.Id : (_db.Sources.Count == 0 ? 1 : _db.Sources.Max(s => s.Id) + 1);
            _db.Sources.RemoveAll(s => s.Id == id);
            _db.Sources.Add(source with { Id = id });
            await SaveAsync(ct);
            return id;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteSourceAsync(int id, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (_db.Sources.RemoveAll(s => s.Id == id) > 0) await SaveAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<long> WriteDetectionsAsync(int videoId, IAsyncEnumerable<Detection> detections, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        var buffer = new List<Detection>();
        await foreach (var d in detections.WithCancellation(ct)) buffer.Add(d with { VideoId = videoId });

        await _gate.WaitAsync(ct);
        try
        {
            _db.Detections.AddRange(buffer);
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
        return buffer.Count;
    }

    public async Task<IReadOnlyList<VideoInfo>> ListVideosAsync(CancellationToken ct = default)
    {
        await LoadAsync(ct);
        return _db.Videos.OrderBy(v => v.Id)
            .Select(v => v with { DetectionCount = _db.Detections.Count(d => d.VideoId == v.Id) }).ToList();
    }

    public async Task<VideoInfo?> GetVideoAsync(int id, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        var v = _db.Videos.FirstOrDefault(x => x.Id == id);
        return v is null ? null : v with { DetectionCount = _db.Detections.Count(d => d.VideoId == id) };
    }

    public async Task<IReadOnlyList<DetectionHit>> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        var rows = Filtered(request.Filter).ToList();
        var limit = Math.Clamp(request.Limit, 1, 500);

        if (request.GroupWindowSeconds <= 0)
        {
            return rows.OrderBy(r => r.OccurredAt).ThenByDescending(r => r.Det.Confidence).Take(limit)
                .Select(r => ToHit(r.Video, [r.Det], r.Det)).ToList();
        }

        var hits = new List<DetectionHit>();
        foreach (var grp in rows.GroupBy(r => (r.Det.VideoId, r.Det.ClassName)))
        {
            var ordered = grp.OrderBy(r => r.Det.TimestampSeconds).ToList();
            var current = new List<Detection>();
            foreach (var r in ordered)
            {
                if (current.Count > 0 && r.Det.TimestampSeconds - current[^1].TimestampSeconds > request.GroupWindowSeconds)
                {
                    hits.Add(ToHit(grp.First().Video, current, current.MaxBy(d => d.Confidence)!));
                    current = [];
                }
                current.Add(r.Det);
            }
            if (current.Count > 0) hits.Add(ToHit(grp.First().Video, current, current.MaxBy(d => d.Confidence)!));
        }
        return hits.OrderBy(h => h.OccurredAt).Take(limit).ToList();
    }

    public async Task<IReadOnlyList<ClassCount>> CountByClassAsync(DetectionFilter filter, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        return Filtered(filter).GroupBy(r => r.Det.ClassName)
            .Select(g => new ClassCount(g.Key, g.LongCount(), g.Min(r => r.Det.TimestampSeconds), g.Max(r => r.Det.TimestampSeconds)))
            .OrderByDescending(c => c.Count).ToList();
    }

    public async Task<IReadOnlyList<TimeBucketCount>> CountByTimeAsync(DetectionFilter filter, TimeSpan bucket, CancellationToken ct = default)
    {
        await LoadAsync(ct);
        var ticks = bucket.Ticks;
        return Filtered(filter)
            .GroupBy(r => (Bucket: new DateTimeOffset(r.OccurredAt.UtcTicks / ticks * ticks, TimeSpan.Zero), r.Det.ClassName))
            .Select(g => new TimeBucketCount(g.Key.Bucket, g.Key.ClassName, g.LongCount()))
            .OrderBy(b => b.BucketStart).ThenByDescending(b => b.Count).ToList();
    }

    private IEnumerable<(VideoInfo Video, Detection Det, DateTimeOffset OccurredAt)> Filtered(DetectionFilter f)
    {
        var videos = _db.Videos.ToDictionary(v => v.Id);
        var classes = f.Classes?.Select(c => c.ToLowerInvariant()).ToHashSet();
        foreach (var d in _db.Detections)
        {
            if (!videos.TryGetValue(d.VideoId, out var v)) continue;
            var at = v.StartedAt.AddSeconds(d.TimestampSeconds);
            if (f.VideoId is { } vid && d.VideoId != vid) continue;
            if (!string.IsNullOrWhiteSpace(f.Camera) && !string.Equals(v.Camera, f.Camera, StringComparison.OrdinalIgnoreCase)) continue;
            if (classes is { Count: > 0 } && !classes.Contains(d.ClassName)) continue;
            if (f.FromSeconds is { } fs && d.TimestampSeconds < fs) continue;
            if (f.ToSeconds is { } ts && d.TimestampSeconds > ts) continue;
            if (f.FromTime is { } ft && at < ft) continue;
            if (f.ToTime is { } tt && at > tt) continue;
            if (f.MinConfidence is { } mc && d.Confidence < mc) continue;
            if (f.MinArea is { } ma && d.Width * d.Height < ma) continue;
            yield return (v, d, at);
        }
    }

    private static DetectionHit ToHit(VideoInfo v, List<Detection> group, Detection best) => new()
    {
        VideoId = v.Id, VideoName = v.Name, Camera = v.Camera,
        TimestampSeconds = group.Min(d => d.TimestampSeconds), EndSeconds = group.Max(d => d.TimestampSeconds), BestSeconds = best.TimestampSeconds,
        OccurredAt = v.StartedAt.AddSeconds(group.Min(d => d.TimestampSeconds)),
        ClassName = best.ClassName, Confidence = best.Confidence, X1 = best.X1, Y1 = best.Y1, X2 = best.X2, Y2 = best.Y2,
        Count = group.Count,
    };

    private async Task LoadAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            if (File.Exists(_path))
            {
                await using var s = File.OpenRead(_path);
                _db = await JsonSerializer.DeserializeAsync<Db>(s, Options, ct) ?? new Db();
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        await using (var s = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(s, _db, Options, ct);
        }
        File.Move(tmp, _path, overwrite: true);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private sealed class Db
    {
        public List<VideoInfo> Videos { get; set; } = [];
        public List<Detection> Detections { get; set; } = [];
        public List<VideoSource> Sources { get; set; } = [];
    }
}
