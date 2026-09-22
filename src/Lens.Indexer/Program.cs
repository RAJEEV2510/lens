using System.Globalization;
using Lens.Core.Indexing;
using Lens.Core.Inference;
using Lens.Core.Storage;
using Microsoft.Extensions.Configuration;

// lens-index <video> [--camera NAME] [--fps 2] [--conf 0.35] [--start 2026-09-22T18:00:00+05:30]
//                    [--classes person,car,truck] [--json detections.json] [--db "Host=..."] [--model path]

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("LENS_")
    .Build();

var opts = CliOptions.Parse(args, config);
if (opts is null) return 1;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

IDetectionStore store = opts.JsonPath is not null
    ? new JsonFileStore(opts.JsonPath)
    : new PostgresStore(opts.ConnectionString!);

Console.WriteLine($"store   : {(opts.JsonPath is not null ? "json " + opts.JsonPath : "postgres")}");
Console.WriteLine($"model   : {opts.ModelPath}");
Console.WriteLine($"video   : {opts.VideoPath}");

await store.EnsureSchemaAsync(cts.Token);

var pipeline = new IndexingPipeline(store, opts.ModelPath, opts.FFmpegDir);
var progress = new Progress<IndexProgress>(p =>
    Console.WriteLine($"  {p.Percent,5:0.0}%  t={p.VideoSeconds,7:0.0}s  frames={p.Frames,6}  detections={p.Detections,7}  {p.FramesPerSecond:0.0} fps"));

var result = await pipeline.IndexAsync(new IndexRequest
{
    VideoPath = opts.VideoPath,
    Camera = opts.Camera,
    SampleFps = opts.SampleFps,
    Confidence = opts.Confidence,
    StartedAt = opts.StartedAt,
    KeepClasses = opts.KeepClasses,
}, progress, cts.Token);

Console.WriteLine();
Console.WriteLine($"done    : video #{result.VideoId}, {result.Frames} frames, {result.Detections} detections in {result.ElapsedSeconds:0.0}s ({result.FramesPerSecond:0.0} frames/s)");
foreach (var (cls, n) in result.PerClass.OrderByDescending(kv => kv.Value).Take(10))
    Console.WriteLine($"          {cls,-14} {n}");
return 0;

sealed record CliOptions
{
    public required string VideoPath { get; init; }
    public string Camera { get; init; } = "default";
    public double SampleFps { get; init; } = 2;
    public float Confidence { get; init; } = 0.35f;
    public DateTimeOffset? StartedAt { get; init; }
    public HashSet<int>? KeepClasses { get; init; }
    public string? JsonPath { get; init; }
    public string? ConnectionString { get; init; }
    public required string ModelPath { get; init; }
    public string? FFmpegDir { get; init; }

    public static CliOptions? Parse(string[] args, IConfiguration config)
    {
        if (args.Length == 0 || args[0].StartsWith("--"))
        {
            Console.Error.WriteLine("usage: lens-index <video> [--camera NAME] [--fps 2] [--conf 0.35] [--start ISO8601] [--classes a,b] [--json out.json] [--db CONN] [--model PATH]");
            return null;
        }

        string? Get(string name)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        var inv = CultureInfo.InvariantCulture;
        var classes = Get("classes");
        HashSet<int>? keep = null;
        if (!string.IsNullOrWhiteSpace(classes))
        {
            keep = [];
            foreach (var c in classes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var id = Array.IndexOf(CocoLabels.Names, c.ToLowerInvariant());
                if (id < 0) { Console.Error.WriteLine($"unknown class '{c}'"); return null; }
                keep.Add(id);
            }
        }

        var json = Get("json");
        var conn = Get("db") ?? config["CONNECTION_STRING"] ?? config.GetConnectionString("Lens");
        if (json is null && string.IsNullOrWhiteSpace(conn))
        {
            Console.Error.WriteLine("no store: pass --json out.json, or --db / LENS_CONNECTION_STRING / ConnectionStrings:Lens");
            return null;
        }

        var model = Get("model") ?? config["MODEL_PATH"] ?? config["Model:Path"] ?? IndexingPipeline.FindDefaultModel();
        if (model is null || !File.Exists(model))
        {
            Console.Error.WriteLine("model not found: run scripts/get-models.ps1 or pass --model");
            return null;
        }

        return new CliOptions
        {
            VideoPath = args[0],
            Camera = Get("camera") ?? "default",
            SampleFps = double.Parse(Get("fps") ?? "2", inv),
            Confidence = float.Parse(Get("conf") ?? "0.35", inv),
            StartedAt = Get("start") is { } s ? DateTimeOffset.Parse(s, inv) : null,
            KeepClasses = keep,
            JsonPath = json,
            ConnectionString = conn,
            ModelPath = model,
            FFmpegDir = config["FFMPEG_DIR"] ?? config["FFmpeg:Dir"],
        };
    }
}
