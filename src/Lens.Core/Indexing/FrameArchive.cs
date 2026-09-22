using Lens.Core.Inference;
using Lens.Core.Video;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Lens.Core.Indexing;

/// <summary>
/// Saves JPEGs of sampled frames so live sources, which cannot be seeked later, still have a picture for every hit.
/// Files are named by frame index under one folder per video, so lookup by time is a small directory probe, not a database query.
/// </summary>
public sealed class FrameArchive
{
    private readonly string _root;
    private readonly JpegEncoder _encoder = new() { Quality = 82 };

    public FrameArchive(string rootDir)
    {
        _root = rootDir;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    /// <summary>Crops the letterbox padding away and writes {root}/{videoId}/{frameIndex}.jpg.</summary>
    public async Task<string> SaveAsync(int videoId, int frameIndex, Frame frame, Letterbox letterbox, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, videoId.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, frameIndex + ".jpg");

        using var image = Image.LoadPixelData<Rgb24>(frame.Rgb, letterbox.TargetSize, letterbox.TargetSize);
        image.Mutate(x => x.Crop(new Rectangle(letterbox.PadX, letterbox.PadY, letterbox.ScaledWidth, letterbox.ScaledHeight)));
        await image.SaveAsync(path, _encoder, ct);
        return path;
    }

    /// <summary>Finds the saved frame closest to a timestamp, within tolerance. Returns null when nothing was archived near that moment.</summary>
    public string? FindNearest(int videoId, double timestampSeconds, double sampleFps, double toleranceSeconds = 1.5)
    {
        var dir = Path.Combine(_root, videoId.ToString());
        if (!Directory.Exists(dir)) return null;

        var target = (int)Math.Round(timestampSeconds * sampleFps);
        var span = (int)Math.Ceiling(toleranceSeconds * sampleFps);
        for (var d = 0; d <= span; d++)
        {
            var a = Path.Combine(dir, (target + d) + ".jpg");
            if (File.Exists(a)) return a;
            if (d > 0)
            {
                var b = Path.Combine(dir, (target - d) + ".jpg");
                if (File.Exists(b)) return b;
            }
        }
        return null;
    }

    /// <summary>Deletes archived frames older than the cutoff. Returns how many files were removed.</summary>
    public int DeleteOlderThan(DateTime cutoffUtc)
    {
        var removed = 0;
        if (!Directory.Exists(_root)) return 0;
        foreach (var f in Directory.EnumerateFiles(_root, "*.jpg", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(f) < cutoffUtc)
            {
                try { File.Delete(f); removed++; } catch (IOException) { }
            }
        }
        return removed;
    }

    public long SizeBytes()
    {
        if (!Directory.Exists(_root)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(_root, "*.jpg", SearchOption.AllDirectories))
            total += new FileInfo(f).Length;
        return total;
    }
}
