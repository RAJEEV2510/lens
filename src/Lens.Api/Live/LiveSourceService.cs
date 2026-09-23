using System.Collections.Concurrent;
using Lens.Core.Indexing;
using Lens.Core.Models;
using Lens.Core.Storage;
using Microsoft.AspNetCore.SignalR;

namespace Lens.Api.Live;

public enum SourceStatus { Connecting, Running, Reconnecting, Disabled, Stopped }

/// <summary>Runtime state of one camera. Mutable; the API serialises snapshots.</summary>
public sealed class SourceState
{
    public SourceStatus Status { get; set; } = SourceStatus.Stopped;
    public DateTimeOffset? ConnectedAt { get; set; }
    public DateTimeOffset? LastFrameAt { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
    public double MeasuredFps { get; set; }
    public long Frames { get; set; }
    public long Detections { get; set; }
    public double InferenceMs { get; set; }
    public int? VideoId { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }

    /// <summary>Sightings per class in the last 60 seconds, for the tile badges.</summary>
    private readonly ConcurrentQueue<(DateTimeOffset At, string Class)> _recent = new();

    public void Record(IEnumerable<string> classes)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var c in classes) _recent.Enqueue((now, c));
        while (_recent.TryPeek(out var head) && now - head.At > TimeSpan.FromSeconds(60)) _recent.TryDequeue(out _);
    }

    public Dictionary<string, int> RecentCounts()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60);
        return _recent.Where(r => r.At >= cutoff).GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Count());
    }
}

/// <summary>
/// Keeps every enabled camera source running: starts a pipeline per source, restarts it with exponential backoff when the
/// stream drops, registers a WebRTC playback path in MediaMTX, and pushes each detection to connected browsers through SignalR.
/// </summary>
public sealed class LiveSourceService : BackgroundService
{
    private sealed class Runner
    {
        public required VideoSource Source { get; set; }
        public required CancellationTokenSource Cts { get; init; }
        public Task Task { get; set; } = Task.CompletedTask;
        public SourceState State { get; } = new();
    }

    private readonly IDetectionStore _store;
    private readonly FrameArchive _archive;
    private readonly IHubContext<LiveHub> _hub;
    private readonly MediaMtxClient _mediaMtx;
    private readonly ILogger<LiveSourceService> _log;
    private readonly ILoggerFactory _loggers;
    private readonly string _modelPath;
    private readonly string? _faceModelPath;
    private readonly string? _ffmpegDir;
    private readonly ConcurrentDictionary<int, Runner> _runners = new();
    private CancellationToken _stopping;

    public LiveSourceService(IDetectionStore store, FrameArchive archive, IHubContext<LiveHub> hub, MediaMtxClient mediaMtx,
        IConfiguration config, ILogger<LiveSourceService> log, ILoggerFactory loggers)
    {
        _store = store;
        _archive = archive;
        _hub = hub;
        _mediaMtx = mediaMtx;
        _log = log;
        _loggers = loggers;
        _ffmpegDir = config["FFMPEG_DIR"];
        _modelPath = config["MODEL_PATH"] ?? config["Lens:ModelPath"] ?? IndexingPipeline.FindDefaultModel()
                     ?? throw new InvalidOperationException("YOLO model not found. Run scripts/get-models.ps1 or set LENS_MODEL_PATH.");
        _faceModelPath = config["FACE_MODEL_PATH"] ?? config["Lens:FaceModelPath"] ?? IndexingPipeline.FindDefaultFaceModel();
        _log.LogInformation("live: face detection {State}", _faceModelPath is null ? "off (no face model)" : "on, " + _faceModelPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var sources = await _store.ListSourcesAsync(stoppingToken);
        foreach (var s in sources) await StartAsync(s, stoppingToken);
        _log.LogInformation("live: {Count} source(s) configured, {Enabled} enabled", sources.Count, sources.Count(s => s.Enabled));

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        foreach (var r in _runners.Values) r.Cts.Cancel();
        await Task.WhenAll(_runners.Values.Select(r => r.Task.ContinueWith(_ => { })));
    }

    public IReadOnlyList<object> Snapshot() =>
        _runners.Values.OrderBy(r => r.Source.Id).Select(r => (object)new
        {
            r.Source.Id, r.Source.Name, r.Source.Camera, r.Source.Url, r.Source.DetectUrl, r.Source.OverlayOffsetMs,
            r.Source.SampleFps, r.Source.Confidence, r.Source.Enabled, r.Source.Simulate,
            status = r.State.Status.ToString().ToLowerInvariant(),
            r.State.ConnectedAt, r.State.LastFrameAt, r.State.LastError, r.State.Attempts, r.State.NextRetryAt,
            measuredFps = Math.Round(r.State.MeasuredFps, 1), r.State.Frames, r.State.Detections,
            inferenceMs = Math.Round(r.State.InferenceMs, 1), r.State.VideoId,
            recent = r.State.RecentCounts(),
            // Playback: WebRTC through MediaMTX when it is up (simulated file sources have no RTSP to proxy), else MJPEG from Lens.
            webrtcPath = r.Source.Simulate ? null : MediaMtxClient.PathName(r.Source.Id),
        }).ToList();

    public async Task<VideoSource> AddAsync(VideoSource source, CancellationToken ct)
    {
        var id = await _store.SaveSourceAsync(source, ct);
        var saved = source with { Id = id };
        await StartAsync(saved, ct);
        return saved;
    }

    /// <summary>Edits a source. Any change to URLs or sampling restarts its pipeline; overlay offset changes do not.</summary>
    public async Task<VideoSource?> UpdateAsync(int id, Func<VideoSource, VideoSource> change, CancellationToken ct)
    {
        if (!_runners.TryGetValue(id, out var runner)) return null;
        var updated = change(runner.Source) with { Id = id };
        await _store.SaveSourceAsync(updated, ct);

        var restart = updated.Url != runner.Source.Url || updated.DetectUrl != runner.Source.DetectUrl
                      || Math.Abs(updated.SampleFps - runner.Source.SampleFps) > 0.001 || Math.Abs(updated.Confidence - runner.Source.Confidence) > 0.001
                      || updated.Enabled != runner.Source.Enabled || updated.Simulate != runner.Source.Simulate;
        if (!restart)
        {
            runner.Source = updated;
            return updated;
        }

        runner.Cts.Cancel();
        await runner.Task.ContinueWith(_ => { });
        _runners.TryRemove(id, out _);
        await StartAsync(updated, ct);
        return updated;
    }

    public async Task<bool> RemoveAsync(int id, CancellationToken ct)
    {
        Runner? runner = null;
        if (_runners.TryRemove(id, out runner))
        {
            runner.Cts.Cancel();
            await runner.Task.ContinueWith(_ => { });
        }
        var existed = (await _store.ListSourcesAsync(ct)).Any(s => s.Id == id);
        await _store.DeleteSourceAsync(id, ct);
        await _mediaMtx.RemovePathAsync(id, ct);
        return existed || runner is not null;
    }

    public Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct) =>
        UpdateAsync(id, s => s with { Enabled = enabled }, ct).ContinueWith(t => t.Result is not null, ct);

    private async Task StartAsync(VideoSource source, CancellationToken ct)
    {
        var runner = new Runner { Source = source, Cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping) };
        _runners[source.Id] = runner;
        if (!source.Enabled)
        {
            runner.State.Status = SourceStatus.Disabled;
            return;
        }
        if (!source.Simulate)
            await _mediaMtx.EnsurePathAsync(source.Id, source.Url, ct);
        runner.Task = Task.Run(() => RunLoopAsync(runner), runner.Cts.Token);
    }

    private async Task RunLoopAsync(Runner runner)
    {
        var ct = runner.Cts.Token;
        var state = runner.State;
        var pipeline = new LivePipeline(_store, _archive, _modelPath, _ffmpegDir, _loggers.CreateLogger<LivePipeline>(), _faceModelPath);

        while (!ct.IsCancellationRequested)
        {
            state.Status = SourceStatus.Connecting;
            state.NextRetryAt = null;
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                _log.LogInformation("live: connecting to {Name} ({Url})", runner.Source.Name, runner.Source.DetectUrl ?? runner.Source.Url);
                await pipeline.RunAsync(runner.Source,
                    onEvent: e => PublishAsync(runner, e),
                    onProgress: p =>
                    {
                        if (state.Status != SourceStatus.Running)
                        {
                            state.Status = SourceStatus.Running;
                            state.ConnectedAt = DateTimeOffset.UtcNow;
                            state.LastError = null;
                        }
                        state.LastFrameAt = p.LastFrameAt;
                        state.MeasuredFps = p.MeasuredFps;
                        state.Frames = p.Frames;
                        state.Detections = p.Detections;
                        state.InferenceMs = p.InferenceMs;
                    },
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.LastError = ex.Message;
                _log.LogWarning("live: {Name} dropped: {Error}", runner.Source.Name, ex.Message);
            }

            if (ct.IsCancellationRequested) break;

            // A run that lasted a while counts as healthy: start the backoff from the beginning.
            if (DateTimeOffset.UtcNow - startedAt > TimeSpan.FromSeconds(60)) state.Attempts = 0;
            state.Attempts++;
            var delay = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Min(state.Attempts - 1, 4))));
            if (runner.Source.Simulate) delay = TimeSpan.FromSeconds(2);   // a looping test file just restarts
            state.Status = SourceStatus.Reconnecting;
            state.NextRetryAt = DateTimeOffset.UtcNow + delay;
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
        state.Status = runner.Source.Enabled ? SourceStatus.Stopped : SourceStatus.Disabled;
    }

    private async Task PublishAsync(Runner runner, LiveDetectionEvent e)
    {
        runner.State.VideoId = e.VideoId;
        runner.State.Record(e.Detections.Select(d => d.ClassName));
        await _hub.Clients.All.SendAsync("detection", new
        {
            sourceId = e.SourceId, videoId = e.VideoId, camera = e.Camera, name = runner.Source.Name,
            frameIndex = e.FrameIndex, timestampSeconds = e.TimestampSeconds, occurredAt = e.OccurredAt, frameSaved = e.FrameSaved,
            detections = e.Detections.Select(d => new { d.ClassName, d.Confidence, d.X1, d.Y1, d.X2, d.Y2 }),
        }, runner.Cts.Token);
    }
}
