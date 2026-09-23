using System.Text;
using Lens.Core.Models;
using Lens.Core.Storage;

namespace Lens.Core.Rag;

public sealed record RagIndexProgress(int Videos, int VideosDone, int Events, int Embedded, bool Running, string? Error, DateTimeOffset? FinishedAt);

/// <summary>
/// Builds the vector index from the detection store: every merged event becomes one sentence and one vector.
/// Incremental, so re-running after new footage only embeds what is new.
/// </summary>
public sealed class RagIndexer
{
    private readonly IDetectionStore _store;
    private readonly IEmbedder _embedder;
    private readonly FileVectorIndex _index;
    private RagIndexProgress _progress = new(0, 0, 0, 0, false, null, null);
    private int _running;

    public RagIndexer(IDetectionStore store, IEmbedder embedder, FileVectorIndex index)
    {
        _store = store;
        _embedder = embedder;
        _index = index;
    }

    public RagIndexProgress Progress => _progress with { Embedded = _index.Count };
    public FileVectorIndex Index => _index;
    public IEmbedder Embedder => _embedder;

    /// <summary>Starts a build in the background unless one is already running. Returns false if it was.</summary>
    public bool Start(bool rebuild = false)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try { await BuildAsync(rebuild, CancellationToken.None); }
            catch (Exception ex) { _progress = _progress with { Running = false, Error = ex.Message, FinishedAt = DateTimeOffset.UtcNow }; }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
        return true;
    }

    public async Task BuildAsync(bool rebuild, CancellationToken ct)
    {
        if (rebuild) await _index.ClearAsync(ct);
        var videos = await _store.ListVideosAsync(ct);
        _progress = new RagIndexProgress(videos.Count, 0, 0, _index.Count, true, null, null);
        var events = 0;
        foreach (var video in videos)
        {
            var batch = new List<EventDocument>();
            await foreach (var doc in DocumentsAsync(video, ct))
            {
                events++;
                if (_index.Contains(doc.Id)) continue;
                batch.Add(doc);
                if (batch.Count >= 64) { await FlushAsync(batch, ct); batch.Clear(); }
                _progress = _progress with { Events = events };
            }
            if (batch.Count > 0) await FlushAsync(batch, ct);
            _progress = _progress with { VideosDone = _progress.VideosDone + 1, Events = events };
        }
        _progress = _progress with { Running = false, FinishedAt = DateTimeOffset.UtcNow };
    }

    private async Task FlushAsync(List<EventDocument> batch, CancellationToken ct)
    {
        var vectors = await _embedder.EmbedAsync(batch.Select(d => d.Text).ToList(), ct);
        await _index.AddAsync(batch, vectors, ct);
    }

    /// <summary>Every merged event in a video, paged by time so videos with thousands of events are covered fully.</summary>
    public async IAsyncEnumerable<EventDocument> DocumentsAsync(VideoInfo video, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var classes = await _store.CountByClassAsync(new DetectionFilter { VideoId = video.Id }, ct);
        foreach (var cls in classes)
        {
            double? from = null;
            while (true)
            {
                var page = await _store.SearchAsync(new SearchRequest
                {
                    Filter = new DetectionFilter { VideoId = video.Id, Classes = [cls.ClassName], FromSeconds = from },
                    Limit = 500, GroupWindowSeconds = 2.0,
                }, ct);
                foreach (var hit in page) yield return ToDocument(video, hit);
                if (page.Count < 500) break;
                from = page[^1].EndSeconds + 0.01;
            }
        }
    }

    public static EventDocument ToDocument(VideoInfo video, DetectionHit h)
    {
        var w = h.X2 - h.X1; var ht = h.Y2 - h.Y1;
        var area = w * ht;
        var size = area > 0.08 ? "large" : area > 0.015 ? "medium-sized" : "small";
        var cx = (h.X1 + h.X2) / 2; var cy = (h.Y1 + h.Y2) / 2;
        var horiz = cx < 0.33 ? "left" : cx > 0.66 ? "right" : "centre";
        var vert = cy < 0.33 ? "top" : cy > 0.66 ? "bottom" : "middle";
        var duration = Math.Max(0.5, h.EndSeconds - h.TimestampSeconds);
        var sb = new StringBuilder();
        sb.Append($"A {size} {h.ClassName} on camera {video.Camera} ({video.Name}) ");
        sb.Append($"at {(int)(h.TimestampSeconds / 60)}:{(int)(h.TimestampSeconds % 60):00} into the video, {h.OccurredAt:HH:mm:ss} on {h.OccurredAt:dddd d MMMM yyyy}, ");
        sb.Append($"in view for {duration:0.#} seconds ({h.Count} sightings, {h.Confidence:P0} confidence), ");
        sb.Append($"in the {vert} {horiz} of the frame.");
        return new EventDocument
        {
            Id = $"{video.Id}:{h.ClassName}:{h.TimestampSeconds:0.0}",
            Text = sb.ToString(),
            VideoId = video.Id, VideoName = video.Name, Camera = video.Camera, ClassName = h.ClassName,
            StartSeconds = h.TimestampSeconds, EndSeconds = h.EndSeconds, BestSeconds = h.BestSeconds, OccurredAt = h.OccurredAt,
            Count = h.Count, Confidence = h.Confidence, X1 = h.X1, Y1 = h.Y1, X2 = h.X2, Y2 = h.Y2,
        };
    }
}
