using Lens.Core.Models;

namespace Lens.Core.Storage;

/// <summary>Filters shared by search and count. All optional; null means no filter.</summary>
public sealed record DetectionFilter
{
    public int? VideoId { get; init; }
    public string? Camera { get; init; }
    public IReadOnlyList<string>? Classes { get; init; }
    public double? FromSeconds { get; init; }
    public double? ToSeconds { get; init; }
    public DateTimeOffset? FromTime { get; init; }
    public DateTimeOffset? ToTime { get; init; }
    public float? MinConfidence { get; init; }
    /// <summary>Minimum box area as a fraction of the frame, to drop tiny far-away objects.</summary>
    public float? MinArea { get; init; }
}

public sealed record SearchRequest
{
    public DetectionFilter Filter { get; init; } = new();
    public int Limit { get; init; } = 50;
    /// <summary>Merge detections closer than this many seconds into one event per class. 0 disables.</summary>
    public double GroupWindowSeconds { get; init; } = 2.0;
}

/// <summary>A detection with its video context, as returned by search.</summary>
public sealed record DetectionHit
{
    public required int VideoId { get; init; }
    public required string VideoName { get; init; }
    public required string Camera { get; init; }
    public required double TimestampSeconds { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string ClassName { get; init; }
    public required float Confidence { get; init; }
    public required float X1 { get; init; }
    public required float Y1 { get; init; }
    public required float X2 { get; init; }
    public required float Y2 { get; init; }
    /// <summary>How many raw detections were merged into this hit.</summary>
    public int Count { get; init; } = 1;
    public double EndSeconds { get; init; }
    /// <summary>Timestamp of the sighting the box and confidence belong to. Show this frame, not TimestampSeconds.</summary>
    public double BestSeconds { get; init; }
}

public sealed record ClassCount(string ClassName, long Count, double FirstSeconds, double LastSeconds);

public sealed record TimeBucketCount(DateTimeOffset BucketStart, string ClassName, long Count);

public interface IDetectionStore
{
    Task EnsureSchemaAsync(CancellationToken ct = default);
    /// <summary>Insert or update a video by path. With clearDetections (the default for file re-indexing) its old detections are dropped; live sources keep theirs.</summary>
    Task<int> UpsertVideoAsync(VideoInfo video, bool clearDetections = true, CancellationToken ct = default);
    Task UpdateVideoDurationAsync(int id, double durationSeconds, CancellationToken ct = default);
    Task<IReadOnlyList<VideoSource>> ListSourcesAsync(CancellationToken ct = default);
    Task<int> SaveSourceAsync(VideoSource source, CancellationToken ct = default);
    Task DeleteSourceAsync(int id, CancellationToken ct = default);
    Task<long> WriteDetectionsAsync(int videoId, IAsyncEnumerable<Detection> detections, CancellationToken ct = default);
    Task<IReadOnlyList<VideoInfo>> ListVideosAsync(CancellationToken ct = default);
    Task<VideoInfo?> GetVideoAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<DetectionHit>> SearchAsync(SearchRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ClassCount>> CountByClassAsync(DetectionFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<TimeBucketCount>> CountByTimeAsync(DetectionFilter filter, TimeSpan bucket, CancellationToken ct = default);
}
