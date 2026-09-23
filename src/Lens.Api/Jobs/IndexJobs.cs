using System.Collections.Concurrent;
using System.Threading.Channels;
using Lens.Core.Indexing;
using Lens.Core.Storage;

namespace Lens.Api.Jobs;

public enum JobStatus { Queued, Running, Done, Failed }

/// <summary>One upload-and-index job. Mutable so the worker can update progress in place; the API serialises snapshots.</summary>
public sealed class IndexJob
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string StoredPath { get; init; }
    public required IndexRequest Request { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public double Percent { get; set; }
    public int Frames { get; set; }
    public long Detections { get; set; }
    public double FramesPerSecond { get; set; }
    public int? VideoId { get; set; }
    public string? Error { get; set; }
    public IReadOnlyDictionary<string, long>? PerClass { get; set; }

    public object Snapshot() => new
    {
        id = Id, fileName = FileName, camera = Request.Camera, status = Status.ToString().ToLowerInvariant(),
        percent = Math.Round(Percent, 1), frames = Frames, detections = Detections, framesPerSecond = Math.Round(FramesPerSecond, 1),
        videoId = VideoId, error = Error, perClass = PerClass, createdAt = CreatedAt, startedAt = StartedAt, finishedAt = FinishedAt,
    };
}

/// <summary>In-memory queue of jobs. One worker drains it, because inference is CPU bound and two jobs at once just halves the speed of both.</summary>
public sealed class IndexQueue
{
    private readonly Channel<IndexJob> _channel = Channel.CreateUnbounded<IndexJob>();
    private readonly ConcurrentDictionary<string, IndexJob> _jobs = new();

    public IndexJob Enqueue(string fileName, string storedPath, IndexRequest request)
    {
        var job = new IndexJob { Id = Guid.NewGuid().ToString("N")[..12], FileName = fileName, StoredPath = storedPath, Request = request };
        _jobs[job.Id] = job;
        _channel.Writer.TryWrite(job);
        return job;
    }

    public IndexJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public IReadOnlyList<IndexJob> All() => _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    public IAsyncEnumerable<IndexJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public sealed class IndexWorker : BackgroundService
{
    private readonly IndexQueue _queue;
    private readonly IDetectionStore _store;
    private readonly string _modelPath;
    private readonly string? _faceModelPath;
    private readonly string? _ffmpegDir;
    private readonly ILogger<IndexWorker> _log;

    public IndexWorker(IndexQueue queue, IDetectionStore store, IConfiguration config, ILogger<IndexWorker> log)
    {
        _queue = queue;
        _store = store;
        _log = log;
        _ffmpegDir = config["FFMPEG_DIR"];
        _modelPath = config["MODEL_PATH"] ?? config["Lens:ModelPath"] ?? IndexingPipeline.FindDefaultModel()
                     ?? throw new InvalidOperationException("YOLO model not found. Run scripts/get-models.ps1 or set LENS_MODEL_PATH.");
        _faceModelPath = config["FACE_MODEL_PATH"] ?? config["Lens:FaceModelPath"] ?? IndexingPipeline.FindDefaultFaceModel();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeline = new IndexingPipeline(_store, _modelPath, _ffmpegDir, _faceModelPath);
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTimeOffset.UtcNow;
            _log.LogInformation("indexing job {Job}: {File} as camera {Camera}", job.Id, job.FileName, job.Request.Camera);
            try
            {
                var progress = new Progress<IndexProgress>(p =>
                {
                    job.Percent = p.Percent;
                    job.Frames = p.Frames;
                    job.Detections = p.Detections;
                    job.FramesPerSecond = p.FramesPerSecond;
                });
                var result = await pipeline.IndexAsync(job.Request, progress, stoppingToken);
                job.VideoId = result.VideoId;
                job.Frames = result.Frames;
                job.Detections = result.Detections;
                job.FramesPerSecond = result.FramesPerSecond;
                job.PerClass = result.PerClass;
                job.Percent = 100;
                job.Status = JobStatus.Done;
                _log.LogInformation("job {Job} done: video #{Video}, {Frames} frames, {Detections} detections, {Fps:0.0} fps",
                    job.Id, result.VideoId, result.Frames, result.Detections, result.FramesPerSecond);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                job.Status = JobStatus.Failed;
                job.Error = "server shutting down";
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "job {Job} failed", job.Id);
                job.Status = JobStatus.Failed;
                job.Error = ex.Message;
            }
            finally
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}
