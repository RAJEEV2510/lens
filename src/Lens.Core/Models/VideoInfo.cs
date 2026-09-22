namespace Lens.Core.Models;

public sealed record VideoInfo
{
    public int Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public string Camera { get; init; } = "default";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public double DurationSeconds { get; init; }
    /// <summary>Wall-clock time the footage starts at. Lets "after 6pm" style questions work.</summary>
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset IndexedAt { get; init; }
    public long DetectionCount { get; init; }
    /// <summary>True for a camera stream: Path is a URL, DurationSeconds keeps growing, frames come from the archive not from seeking.</summary>
    public bool IsLive { get; init; }
}

/// <summary>A configured camera or stream that Lens watches continuously.</summary>
public sealed record VideoSource
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public string Camera { get; init; } = "default";
    /// <summary>rtsp://, rtmp://, http:// stream URL, or a local file path when Simulate is set. Used for playback.</summary>
    public required string Url { get; init; }
    /// <summary>Optional lower-resolution sub-stream used for detection instead of Url. Saves a lot of CPU on multi-camera setups.</summary>
    public string? DetectUrl { get; init; }
    /// <summary>Shift applied when drawing live boxes over the video, to compensate for playback latency. Per camera, user tuned.</summary>
    public int OverlayOffsetMs { get; init; } = 300;
    public double SampleFps { get; init; } = 2;
    public float Confidence { get; init; } = 0.35f;
    public bool Enabled { get; init; } = true;
    /// <summary>Read a local file at real-time pace as if it were a camera. For testing without hardware.</summary>
    public bool Simulate { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
