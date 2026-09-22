using Lens.Core.Indexing;
using Lens.Core.Inference;
using Lens.Core.Video;

namespace Lens.Tests;

public class FrameArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lens-frames-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saves_a_cropped_jpeg_and_finds_it_by_time()
    {
        var archive = new FrameArchive(_root);
        var letterbox = new Letterbox(1280, 720, 640);            // 640x360 content, 140px padding top and bottom
        var rgb = new byte[640 * 640 * 3];
        Array.Fill(rgb, (byte)90);
        var frame = new Frame(rgb, 44, 22.0);

        var path = await archive.SaveAsync(videoId: 7, frameIndex: 44, frame, letterbox);

        Assert.True(File.Exists(path));
        Assert.EndsWith(Path.Combine("7", "44.jpg"), path);
        using var img = SixLabors.ImageSharp.Image.Load(path);
        Assert.Equal(640, img.Width);
        Assert.Equal(360, img.Height);

        Assert.Equal(path, archive.FindNearest(7, 22.0, sampleFps: 2));
        Assert.Equal(path, archive.FindNearest(7, 22.6, sampleFps: 2));     // within tolerance, nearest index
        Assert.Null(archive.FindNearest(7, 30.0, sampleFps: 2));           // too far away
        Assert.Null(archive.FindNearest(8, 22.0, sampleFps: 2));           // other video
    }

    [Fact]
    public void Nearest_prefers_the_closest_index_on_either_side()
    {
        var archive = new FrameArchive(_root);
        var dir = Path.Combine(_root, "3");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "10.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(dir, "14.jpg"), [1]);

        Assert.EndsWith("10.jpg", archive.FindNearest(3, 5.4, 2));   // index 11 -> 10 is one step away, 14 is three
        Assert.EndsWith("14.jpg", archive.FindNearest(3, 6.6, 2));   // index 13 -> 14 is one step away
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
