using System.Globalization;
using Anthropic;
using Anthropic.Exceptions;
using Lens.Api.Agent;
using Lens.Api.Jobs;
using Lens.Api.Live;
using Lens.Core.Indexing;
using Lens.Core.Models;
using Lens.Core.Inference;
using Lens.Core.Query;
using Lens.Core.Rag;
using Lens.Core.Storage;
using Lens.Core.Video;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("LENS_");

var lens = builder.Configuration.GetSection("Lens");
var connectionString = builder.Configuration["CONNECTION_STRING"] ?? lens["ConnectionString"];
var jsonPath = builder.Configuration["JSON_PATH"] ?? lens["JsonPath"] ?? "lens.json";

builder.Services.AddSingleton<IDetectionStore>(_ =>
    !string.IsNullOrWhiteSpace(connectionString)
        ? new PostgresStore(connectionString)
        : new JsonFileStore(Path.GetFullPath(jsonPath, builder.Environment.ContentRootPath)));
builder.Services.AddSingleton(new FrameSampler(builder.Configuration["FFMPEG_DIR"]));
builder.Services.AddSingleton(new AnthropicClient());
builder.Services.AddSingleton<AgentTools>();
builder.Services.AddSingleton(new AgentOptions
{
    Model = lens["Model"] ?? "claude-opus-5",
    MaxToolRounds = int.TryParse(lens["MaxToolRounds"], out var r) ? r : 8,
});
builder.Services.AddSingleton<LensAgent>();

// Who answers questions: the no-model planner first, then a local open model through Ollama, then Claude if configured.
builder.Services.AddSingleton<QuestionPlanner>();
builder.Services.AddSingleton(new OllamaOptions
{
    Url = builder.Configuration["OLLAMA_URL"] ?? lens["OllamaUrl"] ?? "http://localhost:11434",
    Model = builder.Configuration["OLLAMA_MODEL"] ?? lens["OllamaModel"] ?? "qwen2.5:3b",
});
builder.Services.AddHttpClient<OllamaAgent>();
builder.Services.AddSingleton<OllamaAgent>(sp => new OllamaAgent(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaAgent)),
    sp.GetRequiredService<AgentTools>(), sp.GetRequiredService<OllamaOptions>(), sp.GetRequiredService<ILogger<OllamaAgent>>()));
// Optional RAG: event descriptions embedded by a local model, searched by similarity. Off the default path; the UI has a switch.
var embedModel = builder.Configuration["EMBED_MODEL"] ?? lens["EmbedModel"] ?? "nomic-embed-text";
var vectorPath = Path.GetFullPath(builder.Configuration["VECTOR_INDEX_PATH"] ?? lens["VectorIndexPath"] ?? "../../data/lens-vectors", builder.Environment.ContentRootPath);
builder.Services.AddHttpClient(nameof(OllamaEmbedder));
builder.Services.AddSingleton<IEmbedder>(sp => new OllamaEmbedder(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaEmbedder)), sp.GetRequiredService<OllamaOptions>().Url, embedModel));
builder.Services.AddSingleton(new FileVectorIndex(vectorPath));
builder.Services.AddSingleton<RagIndexer>();
builder.Services.AddSingleton<RagAnswerer>();
builder.Services.AddSingleton(new AskRouterOptions
{
    Provider = builder.Configuration["PROVIDER"] ?? lens["Provider"] ?? "auto",
    RagTopK = int.TryParse(lens["RagTopK"], out var topK) ? topK : 12,
    LogPath = (builder.Configuration["ASK_LOG_PATH"] ?? lens["AskLogPath"]) is { Length: > 0 } lp ? Path.GetFullPath(lp, builder.Environment.ContentRootPath) : null,
});
builder.Services.AddSingleton<AskRouter>();
builder.Services.AddSingleton<IndexQueue>();
builder.Services.AddHostedService<IndexWorker>();

// Live cameras: archived frames on disk, SignalR to the browser, one supervisor keeping every source running.
var framesDir = Path.GetFullPath(builder.Configuration["FRAMES_DIR"] ?? lens["FramesDir"] ?? "../../data/frames", builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new FrameArchive(framesDir));
builder.Services.AddSignalR();
builder.Services.AddSingleton<MediaMtxClient>();
builder.Services.AddSingleton<LiveSourceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveSourceService>());
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// Uploads: videos are large, so lift the default 30 MB body limit to the configured cap.
var maxUploadBytes = (long)(double.TryParse(lens["MaxUploadMb"], out var mb) ? mb : 2048) * 1024 * 1024;
var uploadDir = Path.GetFullPath(builder.Configuration["UPLOAD_DIR"] ?? lens["UploadDir"] ?? "../../data/uploads", builder.Environment.ContentRootPath);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = maxUploadBytes);

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

var store = app.Services.GetRequiredService<IDetectionStore>();
await store.EnsureSchemaAsync();
app.Logger.LogInformation("store: {Store}", string.IsNullOrWhiteSpace(connectionString) ? "json " + jsonPath : "postgres");

var api = app.MapGroup("/api");

api.MapGet("/videos", async (IDetectionStore s, CancellationToken ct) => Results.Ok(await s.ListVideosAsync(ct)));

api.MapGet("/videos/{id:int}", async (int id, IDetectionStore s, CancellationToken ct) =>
    await s.GetVideoAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound());

api.MapGet("/detections/search", async (
    IDetectionStore s, CancellationToken ct,
    int? videoId, string? camera, string? classes, double? fromSeconds, double? toSeconds,
    DateTimeOffset? from, DateTimeOffset? to, float? minConfidence, float? minArea, int? limit, double? group) =>
{
    var req = new SearchRequest
    {
        Filter = new DetectionFilter
        {
            VideoId = videoId, Camera = camera,
            Classes = classes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            FromSeconds = fromSeconds, ToSeconds = toSeconds, FromTime = from, ToTime = to,
            MinConfidence = minConfidence, MinArea = minArea,
        },
        Limit = limit ?? 50,
        GroupWindowSeconds = group ?? 2.0,
    };
    return Results.Ok(await s.SearchAsync(req, ct));
});

api.MapGet("/detections/counts", async (IDetectionStore s, CancellationToken ct, int? videoId, string? classes, double? bucketMinutes) =>
{
    var filter = new DetectionFilter
    {
        VideoId = videoId,
        Classes = classes?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };
    return bucketMinutes is { } m && m > 0
        ? Results.Ok(await s.CountByTimeAsync(filter, TimeSpan.FromMinutes(m), ct))
        : Results.Ok(await s.CountByClassAsync(filter, ct));
});

// A JPEG of one moment in a video, so the UI can show what a hit looked like.
// With annotate=true the detections stored for that moment are drawn on the server, so the picture and the boxes cannot disagree.
api.MapGet("/frame", async (int videoId, double t, bool? annotate, IDetectionStore s, FrameSampler sampler, FrameArchive archive, CancellationToken ct) =>
{
    var video = await s.GetVideoAsync(videoId, ct);
    if (video is null) return Results.NotFound();

    // Live sources cannot be seeked afterwards, so their pictures come from the archive. Files fall back to it too if present.
    var sampleFps = 2.0;
    byte[] jpeg;
    var at = t;
    var archived = archive.FindNearest(videoId, t, sampleFps);
    if (archived is not null)
    {
        if (annotate != true) return Results.File(archived, "image/jpeg");
        jpeg = await File.ReadAllBytesAsync(archived, ct);
        if (int.TryParse(Path.GetFileNameWithoutExtension(archived), out var frameIndex)) at = frameIndex / sampleFps;
    }
    else
    {
        if (video.IsLive || !File.Exists(video.Path)) return Results.NotFound();
        jpeg = await sampler.GrabJpegAsync(video.Path, Math.Clamp(t, 0, Math.Max(0, video.DurationSeconds - 0.1)), 960, ct);
        if (annotate != true) return Results.File(jpeg, "image/jpeg");
    }

    var half = 0.5 / sampleFps + 0.01;
    var hits = await s.SearchAsync(new SearchRequest
    {
        Filter = new DetectionFilter { VideoId = videoId, FromSeconds = at - half, ToSeconds = at + half },
        Limit = 200, GroupWindowSeconds = 0,
    }, ct);
    var dets = hits.Select(h => new Detection(videoId, 0, h.TimestampSeconds, 0, h.ClassName, h.Confidence, h.X1, h.Y1, h.X2, h.Y2));
    var caption = $"{video.Name} · {video.StartedAt.AddSeconds(at):HH:mm:ss} · {(int)(at / 60)}:{(int)(at % 60):00} · {hits.Count} detection(s)";
    return Results.File(FrameAnnotator.Annotate(jpeg, dets, caption), "image/jpeg");
});

api.MapGet("/ask/providers", async (AskRouter router, CancellationToken ct) => Results.Ok(await router.StatusAsync(ct)));

api.MapPost("/ask", async (AskRequest body, AskRouter router, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Question)) return Results.BadRequest(new { error = "question is required" });
    if (body.Question.Length > 1000) return Results.BadRequest(new { error = "question too long" });
    try
    {
        var result = await router.AskAsync(body.Question.Trim(), body.VideoId, body.Mode, ct);
        return Results.Ok(result);
    }
    catch (ProviderUnavailableException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 503);
    }
    catch (HttpRequestException ex)
    {
        app.Logger.LogError(ex, "local model error");
        return Results.Json(new { error = "The local model call failed.", detail = ex.Message }, statusCode: 502);
    }
    catch (AnthropicRateLimitException ex)
    {
        return Results.Json(new { error = "Claude is rate limited right now, try again in a minute.", detail = ex.Message }, statusCode: 429);
    }
    catch (AnthropicApiException ex) when (ex.Message.Contains("authentication_error", StringComparison.OrdinalIgnoreCase)
                                          || ex.Message.Contains("x-api-key", StringComparison.OrdinalIgnoreCase))
    {
        return Results.Json(new { error = "The Claude API key was rejected. Check ANTHROPIC_API_KEY, or unset it to use the local model only." }, statusCode: 503);
    }
    catch (AnthropicApiException ex)
    {
        app.Logger.LogError(ex, "Claude API error");
        return Results.Json(new { error = "The Claude call failed.", detail = ex.Message }, statusCode: 502);
    }
});

// RAG index: build in the background, poll for progress. Incremental; rebuild=true starts from scratch.
api.MapGet("/rag/status", async (RagAnswerer rag, CancellationToken ct) => Results.Ok(await rag.StatusAsync(ct)));

api.MapPost("/rag/index", async (RagIndexer indexer, IEmbedder embedder, bool? rebuild, CancellationToken ct) =>
{
    var (ok, reason) = await embedder.ProbeAsync(ct);
    if (!ok) return Results.Json(new { error = reason }, statusCode: 503);
    var started = indexer.Start(rebuild ?? false);
    return Results.Accepted("/api/rag/status", new { started, indexer.Progress });
});

// Upload a video and queue it for indexing. Returns the job id; poll /api/jobs/{id} for progress.
api.MapPost("/videos/upload", async (HttpRequest request, IndexQueue queue, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data expected" });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "file is required" });

    var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
    string[] allowed = [".mp4", ".mkv", ".mov", ".avi", ".ts", ".m4v", ".webm"];
    if (!allowed.Contains(ext)) return Results.BadRequest(new { error = $"unsupported file type {ext}", allowed });

    var camera = form["camera"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(camera)) camera = "default";
    camera = camera.Trim();
    if (camera.Length > 64) return Results.BadRequest(new { error = "camera name too long" });

    DateTimeOffset? startedAt = null;
    if (DateTimeOffset.TryParse(form["startedAt"].FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)) startedAt = parsed;
    var fps = double.TryParse(form["fps"].FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f is > 0 and <= 10 ? f : 2;
    var conf = float.TryParse(form["confidence"].FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) && c is > 0 and < 1 ? c : 0.35f;

    HashSet<int>? keep = null;
    var classes = form["classes"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(classes))
    {
        keep = [];
        foreach (var name in classes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var id = Array.IndexOf(CocoLabels.Names, name.ToLowerInvariant());
            if (id < 0) return Results.BadRequest(new { error = $"unknown class '{name}'" });
            keep.Add(id);
        }
    }

    // Store under a safe generated name; keep the original name for display.
    Directory.CreateDirectory(uploadDir);
    var safeBase = string.Concat(Path.GetFileNameWithoutExtension(file.FileName).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')).Trim('.');
    if (safeBase.Length == 0) safeBase = "video";
    var storedPath = Path.Combine(uploadDir, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeBase[..Math.Min(safeBase.Length, 40)]}{ext}");
    await using (var target = File.Create(storedPath))
        await file.CopyToAsync(target, ct);

    var job = queue.Enqueue(file.FileName, storedPath, new IndexRequest
    {
        VideoPath = storedPath,
        Name = file.FileName,
        Camera = camera,
        StartedAt = startedAt,
        SampleFps = fps,
        Confidence = conf,
        KeepClasses = keep,
    });
    return Results.Accepted($"/api/jobs/{job.Id}", job.Snapshot());
});

api.MapGet("/jobs", (IndexQueue queue) => Results.Ok(queue.All().Select(j => j.Snapshot())));

api.MapGet("/jobs/{id}", (string id, IndexQueue queue) =>
    queue.Get(id) is { } job ? Results.Ok(job.Snapshot()) : Results.NotFound());

// Live camera sources. Adding one starts it immediately; it keeps reconnecting until removed or disabled.
api.MapGet("/sources", (LiveSourceService live) => Results.Ok(live.Snapshot()));

static IResult? ValidateSource(SourceRequest body, out string url, out bool simulate)
{
    url = (body.Url ?? "").Trim();
    simulate = body.Simulate ?? false;
    if (url.Length == 0) return Results.BadRequest(new { error = "url is required" });
    if (!FrameSampler.IsStreamUrl(url))
    {
        if (!File.Exists(url)) return Results.BadRequest(new { error = "url must be an rtsp://, rtmp://, http:// stream, or an existing local file for simulation" });
        simulate = true;
    }
    if (!string.IsNullOrWhiteSpace(body.DetectUrl) && !FrameSampler.IsStreamUrl(body.DetectUrl.Trim()))
        return Results.BadRequest(new { error = "detectUrl must be a stream URL" });
    return null;
}

api.MapPost("/sources", async (SourceRequest body, LiveSourceService live, CancellationToken ct) =>
{
    if (ValidateSource(body, out var url, out var simulate) is { } bad) return bad;
    var source = new VideoSource
    {
        Name = string.IsNullOrWhiteSpace(body.Name) ? (simulate ? Path.GetFileName(url) : "camera") : body.Name.Trim(),
        Camera = string.IsNullOrWhiteSpace(body.Camera) ? "default" : body.Camera.Trim(),
        Url = url,
        DetectUrl = string.IsNullOrWhiteSpace(body.DetectUrl) ? null : body.DetectUrl.Trim(),
        OverlayOffsetMs = body.OverlayOffsetMs is >= -2000 and <= 5000 ? body.OverlayOffsetMs.Value : 300,
        SampleFps = body.SampleFps is > 0 and <= 10 ? body.SampleFps.Value : 2,
        Confidence = body.Confidence is > 0 and < 1 ? body.Confidence.Value : 0.35f,
        Simulate = simulate,
        Enabled = true,
    };
    var saved = await live.AddAsync(source, ct);
    return Results.Created($"/api/sources/{saved.Id}", saved);
});

api.MapPut("/sources/{id:int}", async (int id, SourceRequest body, LiveSourceService live, CancellationToken ct) =>
{
    if (ValidateSource(body, out var url, out var simulate) is { } bad) return bad;
    var updated = await live.UpdateAsync(id, s => s with
    {
        Name = string.IsNullOrWhiteSpace(body.Name) ? s.Name : body.Name.Trim(),
        Camera = string.IsNullOrWhiteSpace(body.Camera) ? s.Camera : body.Camera.Trim(),
        Url = url,
        DetectUrl = string.IsNullOrWhiteSpace(body.DetectUrl) ? null : body.DetectUrl.Trim(),
        OverlayOffsetMs = body.OverlayOffsetMs is >= -2000 and <= 5000 ? body.OverlayOffsetMs.Value : s.OverlayOffsetMs,
        SampleFps = body.SampleFps is > 0 and <= 10 ? body.SampleFps.Value : s.SampleFps,
        Confidence = body.Confidence is > 0 and < 1 ? body.Confidence.Value : s.Confidence,
        Simulate = simulate,
    }, ct);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});

api.MapPost("/sources/{id:int}/enable", async (int id, LiveSourceService live, CancellationToken ct) =>
    await live.SetEnabledAsync(id, true, ct) ? Results.Ok() : Results.NotFound());

api.MapPost("/sources/{id:int}/disable", async (int id, LiveSourceService live, CancellationToken ct) =>
    await live.SetEnabledAsync(id, false, ct) ? Results.Ok() : Results.NotFound());

api.MapDelete("/sources/{id:int}", async (int id, LiveSourceService live, CancellationToken ct) =>
    await live.RemoveAsync(id, ct) ? Results.Ok() : Results.NotFound());

// Analytics view: the exact frames the detector processed, boxes drawn on the server. Single frame, or MJPEG at the detector's rate.
api.MapGet("/sources/{id:int}/annotated.jpg", (int id, LiveSourceService live) =>
    live.AnnotatedJpeg(id) is { } f
        ? Results.File(f.Jpeg, "image/jpeg")
        : Results.NotFound(new { error = "no frame processed yet for this source" }));

api.MapGet("/sources/{id:int}/annotated", async (int id, HttpContext http, LiveSourceService live, CancellationToken ct) =>
{
    http.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
    http.Response.Headers.CacheControl = "no-store";
    await http.Response.StartAsync(ct);
    try { await live.StreamAnnotatedAsync(id, http.Response.Body, ct); }
    catch (OperationCanceledException) { }
    catch (IOException) { }
    return Results.Empty;
});

// MJPEG fallback player: one ffmpeg per viewer, 5 fps, killed when the browser disconnects. Used when MediaMTX is not available.
api.MapGet("/sources/{id:int}/mjpeg", async (int id, HttpContext http, IDetectionStore s, FrameSampler sampler, CancellationToken ct) =>
{
    var source = (await s.ListSourcesAsync(ct)).FirstOrDefault(x => x.Id == id);
    if (source is null) return Results.NotFound();

    http.Response.ContentType = "multipart/x-mixed-replace; boundary=ffmpeg";
    http.Response.Headers.CacheControl = "no-store";
    await http.Response.StartAsync(ct);
    try
    {
        await sampler.StreamMjpegAsync(source.Url, http.Response.Body, fps: 5, maxWidth: 960, source.Simulate, ct);
    }
    catch (OperationCanceledException) { }
    catch (IOException) { }   // client went away
    return Results.Empty;
});

api.MapGet("/status", async (LiveSourceService live, IndexQueue queue, FrameArchive archive, MediaMtxClient mtx, CancellationToken ct) => Results.Ok(new
{
    sources = live.Snapshot(),
    jobs = queue.All().Take(10).Select(j => j.Snapshot()),
    framesArchiveBytes = archive.SizeBytes(),
    uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds),
    mediamtx = new { available = await mtx.IsAvailableAsync(ct), webrtcBaseUrl = mtx.WebRtcBaseUrl },
}));

app.MapHub<LiveHub>("/hubs/live");

// Angular client-side routes (/live, /search, ...) all serve the app shell.
app.MapFallbackToFile("index.html");

app.MapGet("/health", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) }));

app.Run();

public sealed record AskRequest(string Question, int? VideoId, string? Mode);

public sealed record SourceRequest(string Url, string? Name, string? Camera, double? SampleFps, float? Confidence, bool? Simulate,
    string? DetectUrl, int? OverlayOffsetMs);
