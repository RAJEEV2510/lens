using System.Diagnostics;
using System.Threading.Channels;
using Lens.Core.Inference;
using Lens.Core.Models;
using Lens.Core.Storage;
using Lens.Core.Video;

namespace Lens.Core.Indexing;

public sealed record IndexRequest
{
    public required string VideoPath { get; init; }
    public string Camera { get; init; } = "default";
    public double SampleFps { get; init; } = 2;
    public float Confidence { get; init; } = 0.35f;
    public DateTimeOffset? StartedAt { get; init; }
    public HashSet<int>? KeepClasses { get; init; }
    /// <summary>Display name; defaults to the file name.</summary>
    public string? Name { get; init; }
}

public sealed record IndexProgress(int Frames, long Detections, double VideoSeconds, double TotalSeconds, double ElapsedSeconds)
{
    public double Percent => TotalSeconds <= 0 ? 0 : Math.Clamp(VideoSeconds / TotalSeconds * 100, 0, 100);
    public double FramesPerSecond => ElapsedSeconds <= 0 ? 0 : Frames / ElapsedSeconds;
}

public sealed record IndexResult(int VideoId, int Frames, long Detections, double ElapsedSeconds, IReadOnlyDictionary<string, long> PerClass)
{
    public double FramesPerSecond => ElapsedSeconds <= 0 ? 0 : Frames / ElapsedSeconds;
}

/// <summary>
/// The whole indexing job for one video: probe, register, decode frames through ffmpeg, run YOLO, stream detections into the store.
/// Decode and inference run on separate threads joined by a bounded channel, so neither waits on the other.
/// </summary>
public sealed class IndexingPipeline
{
    private readonly IDetectionStore _store;
    private readonly string _modelPath;
    private readonly string? _ffmpegDir;

    public IndexingPipeline(IDetectionStore store, string modelPath, string? ffmpegDir = null)
    {
        _store = store;
        _modelPath = modelPath;
        _ffmpegDir = ffmpegDir;
    }

    public async Task<IndexResult> IndexAsync(IndexRequest request, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var probe = await VideoProbe.ProbeAsync(request.VideoPath, _ffmpegDir, ct);

        var video = new VideoInfo
        {
            Path = Path.GetFullPath(request.VideoPath),
            Name = request.Name ?? Path.GetFileName(request.VideoPath),
            Camera = request.Camera,
            Width = probe.Width,
            Height = probe.Height,
            Fps = probe.Fps,
            DurationSeconds = probe.DurationSeconds,
            StartedAt = request.StartedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(request.VideoPath)),
        };
        var videoId = await _store.UpsertVideoAsync(video, clearDetections: true, ct);

        using var detector = new YoloDetector(new DetectorOptions
        {
            ModelPath = _modelPath,
            ConfidenceThreshold = request.Confidence,
            KeepClasses = request.KeepClasses,
        });
        var letterbox = new Letterbox(probe.Width, probe.Height, detector.InputSize);
        var sampler = new FrameSampler(_ffmpegDir);

        var frames = Channel.CreateBounded<Frame>(new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true });
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in sampler.SampleAsync(request.VideoPath, letterbox, request.SampleFps, ct))
                    await frames.Writer.WriteAsync(f, ct);
                frames.Writer.Complete();
            }
            catch (Exception ex) { frames.Writer.Complete(ex); }
        }, ct);

        var sw = Stopwatch.StartNew();
        var frameCount = 0;
        var detectionCount = 0L;
        var perClass = new Dictionary<string, long>();

        async IAsyncEnumerable<Detection> Detections()
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
            {
                var found = detector.Detect(frame, letterbox, videoId);
                frameCount++;
                foreach (var d in found)
                {
                    detectionCount++;
                    perClass[d.ClassName] = perClass.GetValueOrDefault(d.ClassName) + 1;
                    yield return d;
                }
                if (frameCount % 20 == 0)
                    progress?.Report(new IndexProgress(frameCount, detectionCount, frame.TimestampSeconds, probe.DurationSeconds, sw.Elapsed.TotalSeconds));
            }
        }

        await _store.WriteDetectionsAsync(videoId, Detections(), ct);
        await producer;
        sw.Stop();

        progress?.Report(new IndexProgress(frameCount, detectionCount, probe.DurationSeconds, probe.DurationSeconds, sw.Elapsed.TotalSeconds));
        return new IndexResult(videoId, frameCount, detectionCount, sw.Elapsed.TotalSeconds, perClass);
    }

    /// <summary>Finds models/yolov10n.onnx by walking up from the application directory.</summary>
    public static string? FindDefaultModel()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "models", "yolov10n.onnx");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
