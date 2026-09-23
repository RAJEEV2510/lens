using Lens.Core.Models;
using Lens.Core.Query;
using Lens.Core.Storage;

namespace Lens.Tests;

/// <summary>The no-model question planner: plain English in, store queries and a templated answer out.</summary>
public class QuestionPlannerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "lens-planner-" + Guid.NewGuid().ToString("N") + ".json");
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 18, 0, 0, TimeSpan.FromHours(5.5));

    private async Task<(QuestionPlanner planner, int videoId)> SeedAsync()
    {
        var store = new JsonFileStore(_path);
        var id = await store.UpsertVideoAsync(new VideoInfo
        {
            Path = @"C:\clips\junction.mp4", Name = "junction.mp4", Camera = "gate-2",
            Width = 1600, Height = 1200, Fps = 12, DurationSeconds = 60, StartedAt = Start,
        });
        // A car from 10s to 12s, another car at 30s, a person at 11s. Nothing else.
        var dets = new List<Detection>();
        for (var i = 0; i < 5; i++) dets.Add(D(id, 20 + i, 10 + i * 0.5, 2, "car", 0.6f + i * 0.05f));
        dets.Add(D(id, 60, 30, 2, "car", 0.9f));
        dets.Add(D(id, 22, 11, 0, "person", 0.8f));
        await store.WriteDetectionsAsync(id, ToAsync(dets));
        return (new QuestionPlanner(store), id);
    }

    [Fact]
    public async Task Inventory_question_lists_videos_without_a_model()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("what footage do you have?", null);
        Assert.NotNull(a);
        Assert.Equal("inventory", a.Kind);
        Assert.Contains("junction.mp4", a.Answer);
        Assert.Contains("gate-2", a.Answer);
        Assert.Equal("list_videos", Assert.Single(a.Calls).Tool);
    }

    [Fact]
    public async Task First_sighting_gives_time_and_frame()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("When was the first car seen?", null);
        Assert.NotNull(a);
        Assert.Equal("first", a.Kind);
        Assert.Contains("0:10", a.Answer);
        Assert.Contains("18:00:10", a.Answer);
        Assert.Equal(10, a.Hits[0].TimestampSeconds);
    }

    [Fact]
    public async Task Last_sighting_picks_the_final_event()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("last car", null);
        Assert.NotNull(a);
        Assert.Equal("last", a.Kind);
        Assert.Contains("0:30", a.Answer);
    }

    [Fact]
    public async Task Count_in_first_n_seconds_uses_a_seconds_window_and_counts_events()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("how many cars in the first 20 seconds?", null);
        Assert.NotNull(a);
        Assert.Equal("count", a.Kind);
        Assert.Contains("about 1 separate event", a.Answer);
        Assert.Contains("5 sightings", a.Answer);
        var search = a.Calls.First(c => c.Tool == "search_detections");
        Assert.Equal(20.0, search.Input["to_seconds"]);
        Assert.Equal(new[] { "car" }, (IEnumerable<string>)search.Input["classes"]!);
    }

    [Fact]
    public async Task Clock_time_is_resolved_on_the_footage_day_in_its_own_zone()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("show me people after 6pm", null);
        Assert.NotNull(a);
        Assert.Equal("show", a.Kind);
        Assert.Contains("1 person event", a.Answer);
        var search = a.Calls.First(c => c.Tool == "search_detections");
        Assert.Equal(Start.ToString("o"), search.Input["from_time"]);
        Assert.Equal(11, a.Hits[0].TimestampSeconds);
    }

    [Fact]
    public async Task Exists_question_answers_yes_or_no()
    {
        var (planner, _) = await SeedAsync();
        var no = await planner.TryAnswerAsync("were there any buses?", null);
        Assert.NotNull(no);
        Assert.StartsWith("No, no bus", no.Answer);
        var yes = await planner.TryAnswerAsync("was there any person?", null);
        Assert.NotNull(yes);
        Assert.StartsWith("Yes.", yes.Answer);
    }

    [Fact]
    public async Task Synonyms_and_camera_names_are_understood()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("how many vehicles passed the gate camera?", null);
        Assert.NotNull(a);
        Assert.Equal("count", a.Kind);
        Assert.Contains("Car: about 2 separate events", a.Answer);
        Assert.Contains("in junction.mp4 (gate-2)", a.Answer);
        Assert.Contains("No bus seen", a.Answer);
    }

    [Fact]
    public async Task Unsupported_concepts_get_an_honest_answer_not_a_guess()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("what colour was the car?", null);
        Assert.NotNull(a);
        Assert.Equal("unsupported", a.Kind);
        Assert.Contains("colours", a.Answer);
        Assert.Empty(a.Hits);
    }

    [Fact]
    public async Task Questions_it_cannot_parse_return_null_for_a_model()
    {
        var (planner, _) = await SeedAsync();
        Assert.Null(await planner.TryAnswerAsync("why does this junction get congested?", null));
        Assert.Null(await planner.TryAnswerAsync("hello", null));
    }

    [Fact]
    public async Task Unknown_camera_is_reported()
    {
        var (planner, _) = await SeedAsync();
        var a = await planner.TryAnswerAsync("cars in video 9", null);
        Assert.NotNull(a);
        Assert.Equal("scope", a.Kind);
        Assert.Contains("no video 9", a.Answer);
    }

    private static Detection D(int videoId, int frame, double t, int classId, string name, float conf) =>
        new(videoId, frame, t, classId, name, conf, 0.4f, 0.4f, 0.5f, 0.5f);

    private static async IAsyncEnumerable<Detection> ToAsync(IEnumerable<Detection> items)
    {
        foreach (var d in items) { yield return d; await Task.Yield(); }
    }

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
}
