using Lens.Core.Models;
using Lens.Core.Storage;

namespace Lens.Tests;

public class JsonFileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "lens-tests-" + Guid.NewGuid().ToString("N") + ".json");

    private static readonly DateTimeOffset Start = new(2026, 9, 22, 18, 0, 0, TimeSpan.FromHours(5.5));

    private async Task<(JsonFileStore store, int videoId)> SeedAsync()
    {
        var store = new JsonFileStore(_path);
        var id = await store.UpsertVideoAsync(new VideoInfo
        {
            Path = @"C:\clips\junction.mp4", Name = "junction.mp4", Camera = "gate-2",
            Width = 1600, Height = 1200, Fps = 12, DurationSeconds = 60, StartedAt = Start,
        });

        // A car seen continuously from 10s to 12s (5 frames at 2fps), then again at 30s. A person at 11s.
        var dets = new List<Detection>();
        for (var i = 0; i < 5; i++) dets.Add(D(id, 20 + i, 10 + i * 0.5, 2, "car", 0.6f + i * 0.05f));
        dets.Add(D(id, 60, 30, 2, "car", 0.9f));
        dets.Add(D(id, 22, 11, 0, "person", 0.8f, size: 0.02f));
        await store.WriteDetectionsAsync(id, ToAsync(dets));
        return (store, id);
    }

    [Fact]
    public async Task Search_groups_consecutive_sightings_into_events()
    {
        var (store, id) = await SeedAsync();
        var hits = await store.SearchAsync(new SearchRequest { Filter = new DetectionFilter { Classes = ["car"] }, GroupWindowSeconds = 2 });

        Assert.Equal(2, hits.Count);
        Assert.Equal(10, hits[0].TimestampSeconds);
        Assert.Equal(12, hits[0].EndSeconds);
        Assert.Equal(5, hits[0].Count);
        Assert.Equal(0.8f, hits[0].Confidence, 3);           // best confidence in the group
        Assert.Equal(12, hits[0].BestSeconds);               // the box and frame come from that sighting, not the group start
        Assert.Equal(Start.AddSeconds(10), hits[0].OccurredAt);
        Assert.Equal(30, hits[1].TimestampSeconds);
        Assert.Equal(1, hits[1].Count);
        Assert.All(hits, h => Assert.Equal("gate-2", h.Camera));
    }

    [Fact]
    public async Task Search_with_zero_window_returns_raw_rows()
    {
        var (store, _) = await SeedAsync();
        var hits = await store.SearchAsync(new SearchRequest { Filter = new DetectionFilter { Classes = ["car"] }, GroupWindowSeconds = 0 });
        Assert.Equal(6, hits.Count);
    }

    [Fact]
    public async Task Wall_clock_and_seconds_filters_both_work()
    {
        var (store, _) = await SeedAsync();

        var late = await store.SearchAsync(new SearchRequest { Filter = new DetectionFilter { FromSeconds = 20 } });
        Assert.Single(late);
        Assert.Equal("car", late[0].ClassName);

        var window = await store.SearchAsync(new SearchRequest
        {
            Filter = new DetectionFilter { FromTime = Start.AddSeconds(10.9), ToTime = Start.AddSeconds(11.1) },
            GroupWindowSeconds = 0,
        });
        Assert.Equal(2, window.Count); // one car frame and the person, both at 11s
    }

    [Fact]
    public async Task Min_area_drops_small_boxes()
    {
        var (store, _) = await SeedAsync();
        var big = await store.CountByClassAsync(new DetectionFilter { MinArea = 0.01f });
        Assert.DoesNotContain(big, c => c.ClassName == "person");
        Assert.Contains(big, c => c.ClassName == "car" && c.Count == 6);
    }

    [Fact]
    public async Task Time_buckets_count_per_class()
    {
        var (store, _) = await SeedAsync();
        var buckets = await store.CountByTimeAsync(new DetectionFilter(), TimeSpan.FromSeconds(15));
        // 0-15s: 5 car + 1 person, 30-45s: 1 car
        Assert.Equal(3, buckets.Count);
        Assert.Contains(buckets, b => b.ClassName == "car" && b.Count == 5);
        Assert.Contains(buckets, b => b.ClassName == "person" && b.Count == 1);
        Assert.Contains(buckets, b => b.ClassName == "car" && b.Count == 1);
    }

    [Fact]
    public async Task Reindexing_a_video_replaces_its_detections_and_keeps_its_id()
    {
        var (store, id) = await SeedAsync();
        var again = await store.UpsertVideoAsync(new VideoInfo
        {
            Path = @"C:\clips\junction.mp4", Name = "junction.mp4", Camera = "gate-2",
            Width = 1600, Height = 1200, Fps = 12, DurationSeconds = 60, StartedAt = Start,
        });
        Assert.Equal(id, again);
        await store.WriteDetectionsAsync(again, ToAsync([D(again, 1, 0.5, 5, "bus", 0.7f)]));

        var reloaded = new JsonFileStore(_path);
        var videos = await reloaded.ListVideosAsync();
        Assert.Single(videos);
        Assert.Equal(1, videos[0].DetectionCount);
    }

    [Fact]
    public async Task Sources_persist_across_reloads_and_keep_their_ids()
    {
        var store = new JsonFileStore(_path);
        var a = await store.SaveSourceAsync(new VideoSource { Name = "gate", Camera = "gate-1", Url = "rtsp://cam/1" });
        var b = await store.SaveSourceAsync(new VideoSource { Name = "yard", Camera = "yard-1", Url = "rtsp://cam/2", DetectUrl = "rtsp://cam/2sub", OverlayOffsetMs = 450 });
        Assert.Equal(1, a);
        Assert.Equal(2, b);

        // Unrelated writes must not drop sources (this bit us once).
        var vid = await store.UpsertVideoAsync(new VideoInfo { Path = "rtsp://cam/1", Name = "gate", Camera = "gate-1", Width = 1, Height = 1, Fps = 1, DurationSeconds = 0, StartedAt = Start, IsLive = true }, clearDetections: false);
        await store.UpdateVideoDurationAsync(vid, 12.5);
        await store.DeleteSourceAsync(a);

        var reloaded = new JsonFileStore(_path);
        var sources = await reloaded.ListSourcesAsync();
        var only = Assert.Single(sources);
        Assert.Equal(2, only.Id);
        Assert.Equal("rtsp://cam/2sub", only.DetectUrl);
        Assert.Equal(450, only.OverlayOffsetMs);
        Assert.True(only.Enabled);

        var again = await reloaded.SaveSourceAsync(new VideoSource { Name = "new", Url = "rtsp://cam/3" });
        Assert.Equal(3, again);   // ids are never reused
        Assert.Equal(12.5, (await reloaded.GetVideoAsync(vid))!.DurationSeconds);
    }

    private static Detection D(int videoId, int frame, double ts, int cls, string name, float conf, float size = 0.2f) =>
        new(videoId, frame, ts, cls, name, conf, 0.4f, 0.4f, 0.4f + size, 0.4f + size);

    private static async IAsyncEnumerable<Detection> ToAsync(IEnumerable<Detection> items)
    {
        foreach (var i in items) { yield return i; await Task.Yield(); }
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
