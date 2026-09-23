using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Lens.Core.Inference;
using Lens.Core.Models;
using Lens.Core.Storage;

namespace Lens.Core.Query;

/// <summary>One store call the planner made, in the same shape as a model tool call so the UI can show it the same way.</summary>
public sealed record PlannedCall(string Tool, IReadOnlyDictionary<string, object?> Input, int ResultRows, double Ms);

/// <summary>An answer produced without any language model.</summary>
public sealed record LocalAnswer(string Answer, IReadOnlyList<DetectionHit> Hits, IReadOnlyList<PlannedCall> Calls, string Kind);

/// <summary>
/// Answers the common question shapes straight from the detection store: what footage exists, how many of a class,
/// when the first or last one was seen, show me X, was there any X, plus time windows in seconds or wall-clock.
/// Anything it does not understand returns null so a model can take over. It knows only object classes, so questions
/// about colour, plates, identities, speed or direction get an honest "not available" instead of a guess. Faces are a class
/// (from the face model), so "how many faces" is answered; "who is that" is not.
/// </summary>
public sealed class QuestionPlanner
{
    private readonly IDetectionStore _store;

    public QuestionPlanner(IDetectionStore store) => _store = store;

    private enum Intent { Inventory, Count, First, Last, Show, Exists, Summary, Unsupported }

    private sealed class Plan
    {
        public Intent Intent;
        public List<string> Classes = [];
        public int? VideoId;
        public string? Camera;
        public double? FromSeconds, ToSeconds, LastWindowSeconds;
        public (int Hour, int Minute)? FromClock, ToClock;
        public float? MinConfidence, MinArea;
        public List<string> Unsupported = [];
        public bool RickshawNote;
        public string WindowText = "";
    }

    public async Task<LocalAnswer?> TryAnswerAsync(string question, int? videoId, CancellationToken ct = default)
    {
        var plan = Parse(question);
        if (plan is null) return null;
        plan.VideoId ??= videoId;

        var calls = new List<PlannedCall>();
        var videos = await _store.ListVideosAsync(ct);
        if (videos.Count == 0)
            return new LocalAnswer("Nothing has been indexed yet. Upload a video or add a camera first.", [], calls, "empty");

        if (plan.Intent == Intent.Unsupported)
        {
            var what = string.Join(", ", plan.Unsupported.Distinct());
            return new LocalAnswer(
                $"Lens only indexes object classes (person, car, bus, truck, motorcycle and so on). It does not know {what}, so it cannot answer that yet. " +
                "You can still ask when or how often a class was seen, for example \"when was the first bus seen?\".",
                [], calls, "unsupported");
        }

        // Resolve the scope: which videos the question is about.
        if (plan.Camera is null && plan.VideoId is null) plan.Camera = MatchCamera(question, videos);
        var scoped = videos.Where(v => (plan.VideoId is null || v.Id == plan.VideoId) &&
                                       (plan.Camera is null || string.Equals(v.Camera, plan.Camera, StringComparison.OrdinalIgnoreCase))).ToList();
        if (scoped.Count == 0)
        {
            var missing = plan.VideoId is { } id ? $"video {id}" : $"camera \"{plan.Camera}\"";
            return new LocalAnswer($"There is no {missing}. Indexed cameras: {string.Join(", ", videos.Select(v => v.Camera).Distinct())}.", [], calls, "scope");
        }

        if (plan.Intent == Intent.Inventory)
        {
            var perVideo = new List<IReadOnlyList<ClassCount>>();
            foreach (var v in videos) perVideo.Add(await _store.CountByClassAsync(new DetectionFilter { VideoId = v.Id }, ct));
            return Inventory(videos, calls, perVideo);
        }

        var filter = BuildFilter(plan, scoped);
        var scopeText = ScopeText(plan, scoped) + plan.WindowText;

        return plan.Intent switch
        {
            Intent.Count => await CountAsync(plan, filter, scopeText, calls, ct),
            Intent.Summary => await SummaryAsync(filter, scopeText, calls, ct),
            Intent.First or Intent.Last => await FirstLastAsync(plan, filter, scopeText, calls, ct),
            _ => await ShowAsync(plan, filter, scopeText, calls, ct),
        };
    }

    // ---------------------------------------------------------------- parsing

    private static readonly Dictionary<string, string[]> Synonyms = new()
    {
        ["people"] = ["person"], ["persons"] = ["person"], ["pedestrian"] = ["person"], ["pedestrians"] = ["person"],
        ["human"] = ["person"], ["humans"] = ["person"], ["man"] = ["person"], ["men"] = ["person"], ["woman"] = ["person"],
        ["women"] = ["person"], ["someone"] = ["person"], ["anyone"] = ["person"], ["anybody"] = ["person"], ["somebody"] = ["person"],
        ["walker"] = ["person"], ["walkers"] = ["person"], ["guy"] = ["person"], ["guys"] = ["person"], ["kid"] = ["person"],
        ["kids"] = ["person"], ["child"] = ["person"], ["children"] = ["person"],
        ["faces"] = ["face"], ["facial"] = ["face"],
        ["sedan"] = ["car"], ["sedans"] = ["car"], ["hatchback"] = ["car"], ["taxi"] = ["car"], ["taxis"] = ["car"],
        ["cab"] = ["car"], ["cabs"] = ["car"], ["suv"] = ["car"], ["suvs"] = ["car"], ["jeep"] = ["car"],
        ["lorry"] = ["truck"], ["lorries"] = ["truck"], ["tempo"] = ["truck"], ["tempos"] = ["truck"], ["van"] = ["truck"],
        ["vans"] = ["truck"], ["pickup"] = ["truck"], ["pickups"] = ["truck"],
        ["buses"] = ["bus"], ["busses"] = ["bus"], ["coach"] = ["bus"], ["coaches"] = ["bus"],
        ["motorbike"] = ["motorcycle"], ["motorbikes"] = ["motorcycle"], ["scooter"] = ["motorcycle"], ["scooters"] = ["motorcycle"],
        ["scooty"] = ["motorcycle"], ["moped"] = ["motorcycle"], ["mopeds"] = ["motorcycle"], ["biker"] = ["motorcycle"], ["bikers"] = ["motorcycle"],
        ["cycle"] = ["bicycle"], ["cycles"] = ["bicycle"], ["cyclist"] = ["bicycle"], ["cyclists"] = ["bicycle"], ["pushbike"] = ["bicycle"],
        ["bike"] = ["motorcycle", "bicycle"], ["bikes"] = ["motorcycle", "bicycle"],
        ["vehicle"] = ["car", "truck", "bus", "motorcycle"], ["vehicles"] = ["car", "truck", "bus", "motorcycle"],
        ["traffic"] = ["car", "truck", "bus", "motorcycle"], ["automobile"] = ["car", "truck", "bus"], ["automobiles"] = ["car", "truck", "bus"],
        ["auto"] = ["truck", "motorcycle"], ["autos"] = ["truck", "motorcycle"], ["rickshaw"] = ["truck", "motorcycle"],
        ["rickshaws"] = ["truck", "motorcycle"], ["autorickshaw"] = ["truck", "motorcycle"], ["autorickshaws"] = ["truck", "motorcycle"],
        ["animal"] = ["dog", "cat", "cow", "horse", "sheep", "bird"], ["animals"] = ["dog", "cat", "cow", "horse", "sheep", "bird"],
    };

    private static readonly HashSet<string> RickshawWords = ["auto", "autos", "rickshaw", "rickshaws", "autorickshaw", "autorickshaws"];

    private static readonly (Regex Pattern, string What)[] UnsupportedConcepts =
    [
        (new Regex(@"\b(colou?rs?|red|blue|white|black|green|yellow|silver|grey|gray|brown|pink|purple)\b", RegexOptions.Compiled), "colours"),
        (new Regex(@"\b(number ?plates?|licen[cs]e ?plates?|registration|plates?|anpr)\b", RegexOptions.Compiled), "licence plates"),
        (new Regex(@"\b(who (is|was|were|are)|identity|identities|identify|recogni[sz]e|name of)\b", RegexOptions.Compiled), "identities"),
        (new Regex(@"\b(speeds?|speeding|fast|slow|km/?h|kmph|mph)\b", RegexOptions.Compiled), "speed"),
        (new Regex(@"\b(direction|towards?|heading|left to right|right to left|entering|exiting|enters?|exits?|incoming|outgoing|northbound|southbound|eastbound|westbound|turn(ed|ing)?)\b", RegexOptions.Compiled), "direction of travel"),
    ];

    private const string Secs = @"(?:seconds?|secs?|s)";
    private const string Mins = @"(?:minutes?|mins?|m)";
    private const string Num = @"(\d+(?:\.\d+)?)";
    private const string Mss = @"(\d{1,3}):(\d{2})";
    private const string Clock = @"(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.|o'?clock)";

    private static Plan? Parse(string question)
    {
        var q = question.ToLowerInvariant().Replace('’', '\'');
        q = Regex.Replace(q, @"[?!,;""()\[\]]", " ");
        q = Regex.Replace(q, @"\s+", " ").Trim();
        var plan = new Plan();

        foreach (var (pattern, what) in UnsupportedConcepts)
            if (pattern.IsMatch(q)) plan.Unsupported.Add(what);

        // Time windows first, and remove them, so "first" in "the first 30 seconds" is not read as "the first bus".
        q = ExtractClock(q, plan);
        q = ExtractSeconds(q, plan);

        var m = Regex.Match(q, @"\b(?:video|clip|recording|file)\s*#?\s*(\d+)\b");
        if (m.Success) { plan.VideoId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); q = q.Remove(m.Index, m.Length); }

        if (Regex.IsMatch(q, @"\b(confident|high confidence|sure|clearly|clear|certain)\b")) plan.MinConfidence = 0.6f;
        if (Regex.IsMatch(q, @"\b(big|large|close|nearby|close up|near the camera)\b")) plan.MinArea = 0.02f;

        plan.Classes = ExtractClasses(q, plan);
        var hasClass = plan.Classes.Count > 0;

        if (plan.Unsupported.Count > 0) { plan.Intent = Intent.Unsupported; return plan; }

        if (Regex.IsMatch(q, @"\b(how many|how much|count|number of|total|how often|how frequently)\b"))
            plan.Intent = hasClass ? Intent.Count : Intent.Summary;
        else if (hasClass && Regex.IsMatch(q, @"\b(first|earliest|start|beginning|initial)\b"))
            plan.Intent = Intent.First;
        else if (hasClass && Regex.IsMatch(q, @"\b(last|latest|most recent|final|newest)\b"))
            plan.Intent = Intent.Last;
        else if (hasClass && Regex.IsMatch(q, @"\b(is there|was there|were there|are there|any|did any|does any|has any|have any|ever)\b"))
            plan.Intent = Intent.Exists;
        else if (hasClass)
            plan.Intent = Intent.Show; // "when", "show me", "find", "buses after 6pm", or just "buses"
        else if (Regex.IsMatch(q, @"\b(what|which|list|show|do you have|is there)\b.*\b(footage|videos?|cameras?|clips?|recordings?|files?)\b") ||
                 Regex.IsMatch(q, @"^(videos?|cameras?|footage)$"))
            plan.Intent = Intent.Inventory;
        else if (Regex.IsMatch(q, @"\b(what happened|what did you see|what was seen|what is in|what's in|whats in|summary|summari[sz]e|overview|everything|activity|anything)\b"))
            plan.Intent = Intent.Summary;
        else
            return null;

        return plan;
    }

    private static List<string> ExtractClasses(string q, Plan plan)
    {
        var found = new List<string>();
        foreach (var name in CocoLabels.Names.Where(n => n.Contains(' ')))
            if (Regex.IsMatch(q, $@"\b{Regex.Escape(name)}(s|es)?\b")) found.Add(name);

        foreach (var word in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word.Trim('.', '\'', '-');
            if (w.Length < 2) continue;
            if (RickshawWords.Contains(w)) plan.RickshawNote = true;
            if (Synonyms.TryGetValue(w, out var syn)) { found.AddRange(syn); continue; }
            if (Array.IndexOf(CocoLabels.Names, w) >= 0) { found.Add(w); continue; }
            if (w.EndsWith("es") && Array.IndexOf(CocoLabels.Names, w[..^2]) >= 0) { found.Add(w[..^2]); continue; }
            if (w.EndsWith('s') && Array.IndexOf(CocoLabels.Names, w[..^1]) >= 0) found.Add(w[..^1]);
        }
        return found.Distinct().ToList();
    }

    private static string ExtractClock(string q, Plan plan)
    {
        static (int, int)? Parse(Match m, int hourGroup)
        {
            var h = int.Parse(m.Groups[hourGroup].Value, CultureInfo.InvariantCulture);
            var mi = m.Groups[hourGroup + 1].Success ? int.Parse(m.Groups[hourGroup + 1].Value, CultureInfo.InvariantCulture) : 0;
            var suffix = m.Groups[hourGroup + 2].Value.Replace(".", "");
            if (suffix == "pm" && h < 12) h += 12;
            if (suffix == "am" && h == 12) h = 0;
            return h is >= 0 and < 24 && mi is >= 0 and < 60 ? (h, mi) : null;
        }
        var window = new StringBuilder();

        var m = Regex.Match(q, $@"\bbetween {Clock} and {Clock}\b");
        if (m.Success) { plan.FromClock = Parse(m, 1); plan.ToClock = Parse(m, 4); window.Append($" between {Fmt(plan.FromClock)} and {Fmt(plan.ToClock)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:after|from|since|past) {Clock}\b");
        if (m.Success) { plan.FromClock = Parse(m, 1); window.Append($" after {Fmt(plan.FromClock)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:before|until|till|up to|by) {Clock}\b");
        if (m.Success) { plan.ToClock = Parse(m, 1); window.Append($" before {Fmt(plan.ToClock)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:at|around|about|near) {Clock}\b");
        if (m.Success && Parse(m, 1) is { } at)
        {
            var t = new TimeSpan(at.Item1, at.Item2, 0);
            var lo = t - TimeSpan.FromMinutes(5); var hi = t + TimeSpan.FromMinutes(5);
            plan.FromClock = lo < TimeSpan.Zero ? (0, 0) : (lo.Hours, lo.Minutes);
            plan.ToClock = hi.Days > 0 ? (23, 59) : (hi.Hours, hi.Minutes);
            window.Append($" around {Fmt(at)}"); q = q.Remove(m.Index, m.Length);
        }

        m = Regex.Match(q, @"\b(?:in|during) the (morning|afternoon|evening|night)\b");
        if (m.Success)
        {
            (plan.FromClock, plan.ToClock) = m.Groups[1].Value switch
            {
                "morning" => ((5, 0), (12, 0)), "afternoon" => ((12, 0), (17, 0)), "evening" => ((17, 0), (21, 0)), _ => ((21, 0), (23, 59)),
            };
            window.Append($" in the {m.Groups[1].Value}"); q = q.Remove(m.Index, m.Length);
        }

        plan.WindowText += window.ToString();
        return q;
    }

    private static string Fmt((int Hour, int Minute)? c) => c is { } t ? $"{t.Hour:00}:{t.Minute:00}" : "?";

    private static string ExtractSeconds(string q, Plan plan)
    {
        static double Val(Match m, int g, int unitGroup) =>
            double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * (m.Groups[unitGroup].Value.StartsWith('m') ? 60 : 1);
        static double MssVal(Match m, int g) =>
            int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture);
        var window = new StringBuilder();

        var m = Regex.Match(q, $@"\b(?:in |during |within |over |for )?the first {Num} ?({Secs}|{Mins})\b");
        if (m.Success) { plan.ToSeconds = Val(m, 1, 2); window.Append($" in the first {Span(plan.ToSeconds.Value)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:in |during |within |over )?the last {Num} ?({Secs}|{Mins})\b");
        if (m.Success) { plan.LastWindowSeconds = Val(m, 1, 2); window.Append($" in the last {Span(plan.LastWindowSeconds.Value)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\bbetween {Num} and {Num} ?({Secs}|{Mins})\b");
        if (m.Success) { plan.FromSeconds = Val(m, 1, 3); plan.ToSeconds = Val(m, 2, 3); window.Append($" between {MssText(plan.FromSeconds.Value)} and {MssText(plan.ToSeconds.Value)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\bbetween {Mss} and {Mss}\b");
        if (m.Success) { plan.FromSeconds = MssVal(m, 1); plan.ToSeconds = MssVal(m, 3); window.Append($" between {MssText(plan.FromSeconds.Value)} and {MssText(plan.ToSeconds.Value)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:after|from|past|beyond|since) (?:{Num} ?({Secs}|{Mins})|{Mss})(?: (?:in|into|mark))?\b");
        if (m.Success) { plan.FromSeconds = m.Groups[1].Success ? Val(m, 1, 2) : MssVal(m, 3); window.Append($" after {MssText(plan.FromSeconds.Value)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:before|until|till|up to|by|within) (?:{Num} ?({Secs}|{Mins})|{Mss})(?: (?:in|into|mark))?\b");
        if (m.Success) { plan.ToSeconds = m.Groups[1].Success ? Val(m, 1, 2) : MssVal(m, 3); window.Append($" before {MssText(plan.ToSeconds.Value)}"); q = q.Remove(m.Index, m.Length); }

        m = Regex.Match(q, $@"\b(?:at|around|about|near) (?:{Num} ?({Secs}|{Mins})|{Mss})(?: (?:in|into|mark))?\b");
        if (m.Success)
        {
            var at = m.Groups[1].Success ? Val(m, 1, 2) : MssVal(m, 3);
            plan.FromSeconds = Math.Max(0, at - 5); plan.ToSeconds = at + 5;
            window.Append($" around {MssText(at)}"); q = q.Remove(m.Index, m.Length);
        }

        plan.WindowText += window.ToString();
        return q;
    }

    private static string? MatchCamera(string question, IReadOnlyList<VideoInfo> videos)
    {
        var q = question.ToLowerInvariant();
        string[] generic = ["default", "camera", "cam", "video", "live", "main"];
        foreach (var cam in videos.Select(v => v.Camera).Distinct().OrderByDescending(c => c.Length))
        {
            var lower = cam.ToLowerInvariant();
            if (q.Contains(lower)) return cam;
            foreach (var token in lower.Split('-', '_', ' ', '.'))
                if (token.Length >= 4 && !generic.Contains(token) && Regex.IsMatch(q, $@"\b{Regex.Escape(token)}\b")) return cam;
        }
        return null;
    }

    // ---------------------------------------------------------------- execution

    private static DetectionFilter BuildFilter(Plan plan, List<VideoInfo> scoped)
    {
        var from = plan.FromSeconds; var to = plan.ToSeconds;
        if (plan.LastWindowSeconds is { } last && scoped.Count == 1)
            from = Math.Max(0, scoped[0].DurationSeconds - last);

        DateTimeOffset? fromTime = null, toTime = null;
        if (plan.FromClock is not null || plan.ToClock is not null)
        {
            // Clock times are read on the day the footage starts, in the footage's own time zone.
            var anchor = scoped.MinBy(v => v.StartedAt)!.StartedAt;
            var day = new DateTimeOffset(anchor.Year, anchor.Month, anchor.Day, 0, 0, 0, anchor.Offset);
            if (plan.FromClock is { } f) fromTime = day.AddHours(f.Hour).AddMinutes(f.Minute);
            if (plan.ToClock is { } t) toTime = day.AddHours(t.Hour).AddMinutes(t.Minute);
        }

        return new DetectionFilter
        {
            VideoId = scoped.Count == 1 ? scoped[0].Id : plan.VideoId,
            Camera = plan.Camera,
            Classes = plan.Classes.Count > 0 ? plan.Classes : null,
            FromSeconds = from, ToSeconds = to, FromTime = fromTime, ToTime = toTime,
            MinConfidence = plan.MinConfidence, MinArea = plan.MinArea,
        };
    }

    private static string ScopeText(Plan plan, List<VideoInfo> scoped) =>
        scoped.Count == 1 ? $"in {scoped[0].Name} ({scoped[0].Camera})"
        : plan.Camera is not null ? $"on camera {plan.Camera} ({scoped.Count} videos)"
        : $"across {scoped.Count} videos";

    private static LocalAnswer Inventory(IReadOnlyList<VideoInfo> videos, List<PlannedCall> calls, IReadOnlyList<IReadOnlyList<ClassCount>> classCounts)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var sb = new StringBuilder($"{videos.Count} indexed video{(videos.Count == 1 ? "" : "s")}:\n");
        for (var i = 0; i < videos.Count; i++)
        {
            var v = videos[i];
            var top = string.Join(", ", classCounts[i].Take(6).Select(c => $"{c.ClassName} {c.Count}"));
            sb.Append($"• #{v.Id} {v.Name}{(v.IsLive ? " (live)" : "")} — camera {v.Camera}, {MssText(v.DurationSeconds)} long, starts {v.StartedAt:yyyy-MM-dd HH:mm} ({v.StartedAt:zzz}), {v.DetectionCount} detections");
            if (top.Length > 0) sb.Append($": {top}");
            sb.Append('\n');
        }
        calls.Add(new PlannedCall("list_videos", new Dictionary<string, object?>(), videos.Count, sw.Elapsed.TotalMilliseconds));
        return new LocalAnswer(sb.ToString().TrimEnd(), [], calls, "inventory");
    }

    private async Task<LocalAnswer> CountAsync(Plan plan, DetectionFilter filter, string scope, List<PlannedCall> calls, CancellationToken ct)
    {
        var counts = await Count(filter, calls, ct);
        var events = await Search(filter, 500, 2.0, calls, ct);
        var sb = new StringBuilder();
        foreach (var cls in plan.Classes)
        {
            var c = counts.FirstOrDefault(x => x.ClassName == cls);
            var ev = events.Where(e => e.ClassName == cls).ToList();
            if (c is null || ev.Count == 0) { sb.Append($"No {cls} seen {scope}.\n"); continue; }
            sb.Append($"{Cap(cls)}: about {ev.Count} separate {(ev.Count == 1 ? "event" : "events")} ({c.Count} sightings) {scope}, first at {MssText(c.FirstSeconds)}, last at {MssText(c.LastSeconds)}.\n");
        }
        sb.Append("Sightings are per sampled frame (2 per second); an event is one run of consecutive sightings, so it is the better estimate of distinct objects.");
        if (plan.RickshawNote || plan.Classes.Contains("truck"))
            sb.Append(" Auto-rickshaws are usually detected as truck or motorcycle.");
        return new LocalAnswer(sb.ToString(), events.Take(12).ToList(), calls, "count");
    }

    private async Task<LocalAnswer> SummaryAsync(DetectionFilter filter, string scope, List<PlannedCall> calls, CancellationToken ct)
    {
        var counts = await Count(filter, calls, ct);
        var events = await Search(filter, 500, 2.0, calls, ct);
        if (counts.Count == 0) return new LocalAnswer($"Nothing was detected {scope}.", [], calls, "summary");
        var byClass = events.GroupBy(e => e.ClassName).ToDictionary(g => g.Key, g => g.Count());
        var lines = counts.Take(8).Select(c => $"• {c.ClassName}: about {byClass.GetValueOrDefault(c.ClassName)} events ({c.Count} sightings), {MssText(c.FirstSeconds)} to {MssText(c.LastSeconds)}");
        var text = $"Seen {scope}:\n{string.Join("\n", lines)}";
        if (counts.Count > 8) text += $"\n… and {counts.Count - 8} more classes.";
        return new LocalAnswer(text, events.Take(12).ToList(), calls, "summary");
    }

    private async Task<LocalAnswer> FirstLastAsync(Plan plan, DetectionFilter filter, string scope, List<PlannedCall> calls, CancellationToken ct)
    {
        var events = await Search(filter, 500, 2.0, calls, ct);
        var sb = new StringBuilder();
        var picked = new List<DetectionHit>();
        foreach (var cls in plan.Classes)
        {
            var ev = events.Where(e => e.ClassName == cls).ToList();
            if (ev.Count == 0) { sb.Append($"No {cls} seen {scope}.\n"); continue; }
            var e = plan.Intent == Intent.First ? ev[0] : ev[^1];
            var which = plan.Intent == Intent.First ? "First" : "Last";
            sb.Append($"{which} {cls} seen at {MssText(e.TimestampSeconds)} ({e.OccurredAt:HH:mm:ss}) in {e.VideoName} ({e.Camera})");
            if (e.EndSeconds > e.TimestampSeconds) sb.Append($", in view until {MssText(e.EndSeconds)}");
            sb.Append($" — {e.Count} sighting{(e.Count == 1 ? "" : "s")}, {e.Confidence:P0} confidence.");
            if (ev.Count > 1) sb.Append($" {ev.Count} {cls} events in total.");
            sb.Append('\n');
            picked.AddRange(plan.Intent == Intent.First ? ev.Take(6) : ev.TakeLast(6));
        }
        return new LocalAnswer(sb.ToString().TrimEnd(), picked, calls, plan.Intent == Intent.First ? "first" : "last");
    }

    private async Task<LocalAnswer> ShowAsync(Plan plan, DetectionFilter filter, string scope, List<PlannedCall> calls, CancellationToken ct)
    {
        var events = await Search(filter, 200, 2.0, calls, ct);
        var sb = new StringBuilder();
        foreach (var cls in plan.Classes)
        {
            var ev = events.Where(e => e.ClassName == cls).ToList();
            if (ev.Count == 0) { sb.Append(plan.Intent == Intent.Exists ? $"No, no {cls} was seen {scope}.\n" : $"No {cls} seen {scope}.\n"); continue; }
            var head = plan.Intent == Intent.Exists ? "Yes. " : "";
            sb.Append($"{head}{ev.Count} {cls} event{(ev.Count == 1 ? "" : "s")} {scope}: ");
            sb.Append(string.Join(", ", ev.Take(10).Select(e => e.EndSeconds > e.TimestampSeconds ? $"{MssText(e.TimestampSeconds)}–{MssText(e.EndSeconds)}" : MssText(e.TimestampSeconds))));
            if (ev.Count > 10) sb.Append($" … and {ev.Count - 10} more");
            sb.Append($" (clock {ev[0].OccurredAt:HH:mm:ss} onwards).\n");
        }
        if (plan.RickshawNote) sb.Append("Auto-rickshaws are usually detected as truck or motorcycle, so both are included.");
        return new LocalAnswer(sb.ToString().TrimEnd(), events.Take(12).ToList(), calls, plan.Intent == Intent.Exists ? "exists" : "show");
    }

    private async Task<IReadOnlyList<DetectionHit>> Search(DetectionFilter filter, int limit, double group, List<PlannedCall> calls, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hits = await _store.SearchAsync(new SearchRequest { Filter = filter, Limit = limit, GroupWindowSeconds = group }, ct);
        var input = ToInput(filter); input["limit"] = limit; input["group_window_seconds"] = group;
        calls.Add(new PlannedCall("search_detections", input, hits.Count, sw.Elapsed.TotalMilliseconds));
        return hits;
    }

    private async Task<IReadOnlyList<ClassCount>> Count(DetectionFilter filter, List<PlannedCall> calls, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var counts = await _store.CountByClassAsync(filter, ct);
        calls.Add(new PlannedCall("count_detections", ToInput(filter), counts.Count, sw.Elapsed.TotalMilliseconds));
        return counts;
    }

    /// <summary>The filter in the tool-call vocabulary, so a logged local answer looks like a model's tool call and can train one.</summary>
    public static Dictionary<string, object?> ToInput(DetectionFilter f)
    {
        var d = new Dictionary<string, object?>();
        if (f.VideoId is { } v) d["video_id"] = v;
        if (f.Camera is { } c) d["camera"] = c;
        if (f.Classes is { } cl) d["classes"] = cl;
        if (f.FromSeconds is { } fs) d["from_seconds"] = fs;
        if (f.ToSeconds is { } ts) d["to_seconds"] = ts;
        if (f.FromTime is { } ft) d["from_time"] = ft.ToString("o");
        if (f.ToTime is { } tt) d["to_time"] = tt.ToString("o");
        if (f.MinConfidence is { } mc) d["min_confidence"] = mc;
        if (f.MinArea is { } ma) d["min_area"] = ma;
        return d;
    }

    private static string MssText(double s) => $"{(int)(s / 60)}:{(int)(s % 60):00}";
    private static string Span(double s) => s % 60 == 0 && s >= 60 ? $"{(int)(s / 60)} minute{(s == 60 ? "" : "s")}" : $"{s:0.#} seconds";
    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
