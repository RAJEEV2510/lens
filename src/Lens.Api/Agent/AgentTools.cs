using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.Models.Messages;
using Lens.Core.Inference;
using Lens.Core.Storage;

namespace Lens.Api.Agent;

/// <summary>The three tools the agent can call. Each maps to one store query and returns compact JSON.</summary>
public sealed class AgentTools
{
    private readonly IDetectionStore _store;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AgentTools(IDetectionStore store) => _store = store;

    public static IReadOnlyList<Tool> Definitions { get; } =
    [
        new Tool
        {
            Name = "list_videos",
            Description = "List every indexed video with its camera name, duration, wall-clock start time and how many detections it holds. Call this first when you do not know what footage exists.",
            InputSchema = new() { Properties = new Dictionary<string, JsonElement>(), Required = [] },
        },
        new Tool
        {
            Name = "search_detections",
            Description = "Find sightings of object classes in the footage. Consecutive sightings of the same class within group_window_seconds are merged into one event with a start and end time, so the result is a list of events, not raw frames. Use it to answer 'when', 'show me' and 'was there' questions.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["video_id"] = Prop("integer", "Restrict to one video id from list_videos."),
                    ["camera"] = Prop("string", "Restrict to one camera name."),
                    ["classes"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array", items = new { type = "string", @enum = CocoLabels.Names },
                        description = "COCO class names to look for, e.g. [\"car\",\"truck\"]. Omit for all classes.",
                    }),
                    ["from_seconds"] = Prop("number", "Only sightings at or after this many seconds into the video."),
                    ["to_seconds"] = Prop("number", "Only sightings at or before this many seconds into the video."),
                    ["from_time"] = Prop("string", "ISO 8601 wall-clock lower bound, e.g. 2026-09-22T18:00:00+05:30."),
                    ["to_time"] = Prop("string", "ISO 8601 wall-clock upper bound."),
                    ["min_confidence"] = Prop("number", "Drop detections below this confidence (0..1). Default 0.35."),
                    ["min_area"] = Prop("number", "Drop boxes smaller than this fraction of the frame (0..1). Use ~0.01 to ignore distant objects."),
                    ["group_window_seconds"] = Prop("number", "Merge sightings closer than this into one event. Default 2. Use 0 for raw per-frame rows."),
                    ["limit"] = Prop("integer", "Maximum events to return, default 30, max 200."),
                },
                Required = [],
            },
        },
        new Tool
        {
            Name = "count_detections",
            Description = "Count sightings per class, or per time bucket when bucket_minutes is given. Counts are per sampled frame (2 frames per second), so 100 'car' counts could be one car parked for 50 seconds. For 'how many distinct' questions prefer search_detections and count the events.",
            InputSchema = new()
            {
                Properties = new Dictionary<string, JsonElement>
                {
                    ["video_id"] = Prop("integer", "Restrict to one video id."),
                    ["camera"] = Prop("string", "Restrict to one camera name."),
                    ["classes"] = JsonSerializer.SerializeToElement(new
                    {
                        type = "array", items = new { type = "string", @enum = CocoLabels.Names },
                        description = "Class names to count. Omit for all.",
                    }),
                    ["from_seconds"] = Prop("number", "Lower bound in seconds into the video."),
                    ["to_seconds"] = Prop("number", "Upper bound in seconds into the video."),
                    ["from_time"] = Prop("string", "ISO 8601 wall-clock lower bound."),
                    ["to_time"] = Prop("string", "ISO 8601 wall-clock upper bound."),
                    ["min_confidence"] = Prop("number", "Minimum confidence 0..1."),
                    ["bucket_minutes"] = Prop("number", "If set, return counts per time bucket of this many minutes instead of totals."),
                },
                Required = [],
            },
        },
    ];

    /// <summary>Runs a tool. Returns the JSON text for Claude and, for searches, the hits so the UI can render them.</summary>
    public async Task<(string Json, IReadOnlyList<DetectionHit> Hits)> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        switch (name)
        {
            case "list_videos":
            {
                var videos = await _store.ListVideosAsync(ct);
                var payload = new List<object>();
                foreach (var v in videos)
                {
                    var classes = await _store.CountByClassAsync(new DetectionFilter { VideoId = v.Id }, ct);
                    payload.Add(new
                    {
                        video_id = v.Id, name = v.Name, camera = v.Camera,
                        duration_seconds = Math.Round(v.DurationSeconds, 1),
                        started_at = v.StartedAt, ends_at = v.StartedAt.AddSeconds(v.DurationSeconds),
                        detections = v.DetectionCount,
                        classes = classes.Take(12).Select(c => new { c.ClassName, c.Count }),
                    });
                }
                return (JsonSerializer.Serialize(payload, Json), []);
            }
            case "search_detections":
            {
                var req = new SearchRequest
                {
                    Filter = ParseFilter(input),
                    Limit = Int(input, "limit") ?? 30,
                    GroupWindowSeconds = Num(input, "group_window_seconds") ?? 2.0,
                };
                var hits = await _store.SearchAsync(req, ct);
                var payload = hits.Select(h => new
                {
                    video_id = h.VideoId, video = h.VideoName, camera = h.Camera, @class = h.ClassName,
                    start_seconds = Math.Round(h.TimestampSeconds, 1), end_seconds = Math.Round(h.EndSeconds, 1),
                    best_frame_seconds = Math.Round(h.BestSeconds, 1),
                    at = h.OccurredAt, sightings = h.Count, confidence = Math.Round(h.Confidence, 2),
                    box = new { x1 = R(h.X1), y1 = R(h.Y1), x2 = R(h.X2), y2 = R(h.Y2) },
                });
                return (JsonSerializer.Serialize(new { events = payload, returned = hits.Count, limit = req.Limit }, Json), hits);
            }
            case "count_detections":
            {
                var filter = ParseFilter(input);
                if (Num(input, "bucket_minutes") is { } minutes && minutes > 0)
                {
                    var buckets = await _store.CountByTimeAsync(filter, TimeSpan.FromMinutes(minutes), ct);
                    return (JsonSerializer.Serialize(buckets.Select(b => new { bucket_start = b.BucketStart, @class = b.ClassName, b.Count }), Json), []);
                }
                var counts = await _store.CountByClassAsync(filter, ct);
                return (JsonSerializer.Serialize(counts.Select(c => new
                {
                    @class = c.ClassName, sightings = c.Count,
                    first_seconds = Math.Round(c.FirstSeconds, 1), last_seconds = Math.Round(c.LastSeconds, 1),
                }), Json), []);
            }
            default:
                return (JsonSerializer.Serialize(new { error = $"unknown tool {name}" }), []);
        }
    }

    private static DetectionFilter ParseFilter(IReadOnlyDictionary<string, JsonElement> input) => new()
    {
        VideoId = Int(input, "video_id"),
        Camera = Str(input, "camera"),
        Classes = input.TryGetValue("classes", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : null,
        FromSeconds = Num(input, "from_seconds"),
        ToSeconds = Num(input, "to_seconds"),
        FromTime = Time(input, "from_time"),
        ToTime = Time(input, "to_time"),
        MinConfidence = (float?)Num(input, "min_confidence"),
        MinArea = (float?)Num(input, "min_area"),
    };

    private static JsonElement Prop(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });

    private static double? Num(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;

    private static string? Str(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        Str(d, k) is { } s && DateTimeOffset.TryParse(s, out var t) ? t : null;

    private static double R(float v) => Math.Round(v, 3);
}
