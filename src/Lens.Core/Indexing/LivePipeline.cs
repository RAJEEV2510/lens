using System.Diagnostics;
using Lens.Core.Inference;
using Lens.Core.Models;
using Lens.Core.Storage;
using Lens.Core.Video;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lens.Core.Indexing;

/// <summary>What the live pipeline saw in one sampled frame. Pushed to the UI as it happens.</summary>
public sealed record LiveDetectionEvent(
    int SourceId, int VideoId, string Camera, int FrameIndex, double TimestampSeconds, DateTimeOffset OccurredAt,
    IReadOnlyList<Detection> Detections, bool FrameSaved);

/// <summary>One processed frame with what the detector found in it. The pixels are the detector's own input, so boxes always line up.</summary>
public sealed record LiveFrame(int VideoId, Frame Frame, Letterbox Letterbox, IReadOnlyList<Detection> Detections, DateTimeOffset At);

public sealed record LiveProgress(long Frames, long Detections, double MeasuredFps, DateTimeOffset LastFrameAt, double InferenceMs);

/// <summary>
/// Runs one camera stream until it ends, errors, or is cancelled. Timestamps are wall-clock, detections are flushed to the
/// store in small batches, and frames with detections are archived so they can be shown later. Reconnection is the caller's job.
/// </summary>
public sealed class LivePipeline
{
    private readonly IDetectionStore _store;
    private readonly FrameArchive _archive;
    private readonly string _modelPath;
    private readonly string? _faceModelPath;
    private readonly string? _ffmpegDir;
    private readonly ILogger _log;

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Once frames are flowing, this much silence means the stream is dead.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Grace period for the first frame: a decoder has to wait for the camera's next keyframe, which on badly configured streams can be tens of seconds.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public double ArchiveEverySeconds { get; init; } = 1.0;

    public LivePipeline(IDetectionStore store, FrameArchive archive, string modelPath, string? ffmpegDir = null, ILogger? log = null, string? faceModelPath = null)
    {
        _store = store;
        _archive = archive;
        _modelPath = modelPath;
        _faceModelPath = faceModelPath;
        _ffmpegDir = ffmpegDir;
        _log = log ?? NullLogger.Instance;
    }

    public async Task RunAsync(VideoSource source, Func<LiveDetectionEvent, Task>? onEvent, Action<LiveProgress>? onProgress, CancellationToken ct,
        Action<LiveFrame>? onFrame = null)
    {
        // Detection can run on a lower-resolution sub-stream while playback uses the main one.
        var detectUrl = string.IsNullOrWhiteSpace(source.DetectUrl) ? source.Url : source.DetectUrl;
        var probe = await VideoProbe.ProbeAsync(detectUrl, _ffmpegDir, ct);
        if (probe.Width <= 0 || probe.Height <= 0) throw new InvalidOperationException("stream has no video dimensions");

        // One video row per source URL. Reconnects keep the same row and continue the frame numbering from the original start.
        var existing = (await _store.ListVideosAsync(ct)).FirstOrDefault(v => string.Equals(v.Path, source.Url, StringComparison.OrdinalIgnoreCase));
        var startedAt = existing?.StartedAt ?? DateTimeOffset.UtcNow;
        var video = new VideoInfo
        {
            Path = source.Url,
            Name = source.Name,
            Camera = source.Camera,
            Width = probe.Width,
            Height = probe.Height,
            Fps = probe.Fps,
            DurationSeconds = existing?.DurationSeconds ?? 0,
            StartedAt = startedAt,
            IsLive = true,
        };
        var videoId = await _store.UpsertVideoAsync(video, clearDetections: false, ct);

        using var detector = new DetectorSet(_modelPath, _faceModelPath, source.Confidence);
        var letterbox = new Letterbox(probe.Width, probe.Height, detector.InputSize);
        var sampler = new FrameSampler(_ffmpegDir);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastFrameAt = DateTimeOffset.UtcNow;
        var gotFirstFrame = false;
        var watchdog = Task.Run(async () =>
        {
            while (!stall.IsCancellationRequested)
            {
                await Task.Delay(1000, stall.Token).ContinueWith(_ => { });
                var limit = gotFirstFrame ? StallTimeout : ConnectTimeout;
                if (!stall.IsCancellationRequested && DateTimeOffset.UtcNow - lastFrameAt > limit)
                {
                    _log.LogWarning("source {Source}: no frames for {Seconds}s, dropping the connection", source.Name, limit.TotalSeconds);
                    stall.Cancel();
                }
            }
        });

        var buffer = new List<Detection>();
        var lastFlush = DateTimeOffset.UtcNow;
        var lastArchived = double.NegativeInfinity;
        long frames = 0, total = 0;
        var window = Stopwatch.StartNew();
        var windowFrames = 0;
        var measuredFps = 0d;
        var stalled = false;

        try
        {
            await foreach (var raw in sampler.SampleAsync(detectUrl, letterbox, source.SampleFps, source.Simulate, stall.Token))
            {
                var now = DateTimeOffset.UtcNow;
                lastFrameAt = now;
                gotFirstFrame = true;
                var frameIndex = (int)Math.Floor((now - startedAt).TotalSeconds * source.SampleFps);
                var ts = frameIndex / source.SampleFps;
                var frame = new Frame(raw.Rgb, frameIndex, ts);

                var inference = Stopwatch.StartNew();
                var found = detector.Detect(frame, letterbox, videoId);
                inference.Stop();

                frames++;
                windowFrames++;
                total += found.Count;
                if (window.Elapsed.TotalSeconds >= 5)
                {
                    measuredFps = windowFrames / window.Elapsed.TotalSeconds;
                    window.Restart();
                    windowFrames = 0;
                }

                var saved = false;
                if (found.Count > 0 && ts - lastArchived >= ArchiveEverySeconds)
                {
                    await _archive.SaveAsync(videoId, frameIndex, frame, letterbox, ct);
                    lastArchived = ts;
                    saved = true;
                }

                buffer.AddRange(found);
                onFrame?.Invoke(new LiveFrame(videoId, frame, letterbox, found, now));
                onProgress?.Invoke(new LiveProgress(frames, total, measuredFps, now, inference.Elapsed.TotalMilliseconds));

                if (onEvent is not null && found.Count > 0)
                {
                    try { await onEvent(new LiveDetectionEvent(source.Id, videoId, source.Camera, frameIndex, ts, startedAt.AddSeconds(ts), found, saved)); }
                    catch (Exception ex) { _log.LogDebug(ex, "live event handler failed"); }
                }

                if (now - lastFlush >= FlushInterval || buffer.Count >= 500)
                {
                    await FlushAsync(videoId, buffer, ts, ct);
                    lastFlush = now;
                }
            }
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            stalled = true;
        }
        finally
        {
            stall.Cancel();
            await watchdog;
            if (buffer.Count > 0)
            {
                try { await FlushAsync(videoId, buffer, (DateTimeOffset.UtcNow - startedAt).TotalSeconds, CancellationToken.None); }
                catch (Exception ex) { _log.LogWarning(ex, "final flush failed for {Source}", source.Name); }
            }
        }

        if (stalled) throw new TimeoutException($"no frames from {source.Name} for {StallTimeout.TotalSeconds:0}s");
        if (!ct.IsCancellationRequested) throw new EndOfStreamException($"stream {source.Name} ended after {frames} frames");
    }

    private async Task FlushAsync(int videoId, List<Detection> buffer, double durationSeconds, CancellationToken ct)
    {
        if (buffer.Count > 0)
        {
            var batch = buffer.ToArray();
            buffer.Clear();
            await _store.WriteDetectionsAsync(videoId, ToAsync(batch), ct);
        }
        await _store.UpdateVideoDurationAsync(videoId, durationSeconds, ct);
    }

    private static async IAsyncEnumerable<Detection> ToAsync(Detection[] items)
    {
        foreach (var d in items) yield return d;
        await Task.CompletedTask;
    }
}
