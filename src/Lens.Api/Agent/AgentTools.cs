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

    /// <summary>One tool the agent can call, described independently of any model vendor's SDK.</summary>
    public sealed record ToolSpec(string Name, string Description, IReadOnlyDictionary<string, JsonElement> Properties)
    {
        /// <summary>JSON schema object for the tool's input, as OpenAI-style and Ollama tool definitions expect it.</summary>
        public JsonElement ParametersSchema => JsonSerializer.SerializeToElement(new { type = "object", properties = Properties, required = Array.Empty<string>() });
    }

    private static readonly Dictionary<string, JsonElement> FilterProps = new()
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
    };

    public static IReadOnlyList<ToolSpec> Specs { get; } =
    [
        new("list_videos",
            "List every indexed video with its camera name, duration, wall-clock start time and how many detections it holds. Call this first when you do not know what footage exists.",
            new Dictionary<string, JsonElement>()),
        new("search_detections",
            "Find sightings of object classes in the footage. Consecutive sightings of the same class within group_window_seconds are merged into one event with a start and end time, so the result is a list of events, not raw frames. Use it to answer 'when', 'show me' and 'was there' questions.",
            new Dictionary<string, JsonElement>(FilterProps)
            {
                ["min_area"] = Prop("number", "Drop boxes smaller than this fraction of the frame (0..1). Use ~0.01 to ignore distant objects."),
                ["group_window_seconds"] = Prop("number", "Merge sightings closer than this into one event. Default 2. Use 0 for raw per-frame rows."),
                ["limit"] = Prop("integer", "Maximum events to return, default 30, max 200."),
            }),
        new("count_detections",
            "Count sightings per class, or per time bucket when bucket_minutes is given. Counts are per sampled frame (2 frames per second), so 100 'car' counts could be one car parked for 50 seconds. For 'how many distinct' questions prefer search_detections and count the events.",
            new Dictionary<string, JsonElement>(FilterProps)
            {
                ["bucket_minutes"] = Prop("number", "If set, return counts per time bucket of this many minutes instead of totals."),
            }),
    ];

    /// <summary>The same tools in the Anthropic SDK's shape.</summary>
    public static IReadOnlyList<Tool> Definitions { get; } = Specs.Select(t => new Tool
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = new() { Properties = new Dictionary<string, JsonElement>(t.Properties), Required = [] },
    }).ToList();

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
        Classes = Classes(input),
        FromSeconds = Num(input, "from_seconds"),
        ToSeconds = Num(input, "to_seconds"),
        FromTime = Time(input, "from_time"),
        ToTime = Time(input, "to_time"),
        MinConfidence = (float?)Num(input, "min_confidence"),
        MinArea = (float?)Num(input, "min_area"),
    };

    private static JsonElement Prop(string type, string description) =>
        JsonSerializer.SerializeToElement(new { type, description });

    /// <summary>Accepts ["bus","car"], "bus,car" or "bus". Unknown names are dropped so a typo does not turn into an empty result silently elsewhere.</summary>
    private static IReadOnlyList<string>? Classes(IReadOnlyDictionary<string, JsonElement> d)
    {
        if (!d.TryGetValue("classes", out var c)) return null;
        IEnumerable<string> raw = c.ValueKind switch
        {
            JsonValueKind.Array => c.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString()),
            JsonValueKind.String => (c.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
        var list = raw.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        return list.Count == 0 ? null : list;
    }

    private static double? Num(IReadOnlyDictionary<string, JsonElement> d, string k)
    {
        if (!d.TryGetValue(k, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }

    private static int? Int(IReadOnlyDictionary<string, JsonElement> d, string k) => Num(d, k) is { } n ? (int)n : null;

    private static string? Str(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        d.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static DateTimeOffset? Time(IReadOnlyDictionary<string, JsonElement> d, string k) =>
        Str(d, k) is { } s && DateTimeOffset.TryParse(s, out var t) ? t : null;

    private static double R(float v) => Math.Round(v, 3);
}
