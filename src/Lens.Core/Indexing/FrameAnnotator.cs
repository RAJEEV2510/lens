using Lens.Core.Inference;
using Lens.Core.Models;
using Lens.Core.Video;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Lens.Core.Indexing;

/// <summary>
/// Draws detections onto the frame they came from, on the server. What you see is exactly what the detector saw: no
/// playback latency, no overlay offset guesswork. Colours match the web UI so the two views read the same.
/// </summary>
public static class FrameAnnotator
{
    private static readonly JpegEncoder Encoder = new() { Quality = 85 };
    private static readonly Lazy<Font?> LabelFont = new(LoadFont);

    private static readonly Dictionary<string, Color> Colours = new()
    {
        ["person"] = Color.ParseHex("#ff4d4d"), ["car"] = Color.ParseHex("#4dff88"), ["truck"] = Color.ParseHex("#ffd24d"),
        ["bus"] = Color.ParseHex("#4dc3ff"), ["motorcycle"] = Color.ParseHex("#ff8c4d"), ["bicycle"] = Color.ParseHex("#c84dff"),
        ["dog"] = Color.ParseHex("#ff4dc3"), ["face"] = Color.ParseHex("#4dfff0"),
    };

    public static Color ColourFor(string className) => Colours.TryGetValue(className, out var c) ? c : Color.White;

    /// <summary>The detector's letterboxed input, cropped back to the source aspect ratio.</summary>
    public static Image<Rgb24> FromFrame(Frame frame, Letterbox letterbox)
    {
        var image = Image.LoadPixelData<Rgb24>(frame.Rgb, letterbox.TargetSize, letterbox.TargetSize);
        image.Mutate(x => x.Crop(new Rectangle(letterbox.PadX, letterbox.PadY, letterbox.ScaledWidth, letterbox.ScaledHeight)));
        return image;
    }

    /// <summary>Draws boxes and labels in place. Box coordinates are normalised 0..1, so any frame size works.</summary>
    public static void Draw(Image image, IEnumerable<Detection> detections, string? caption = null)
    {
        var w = image.Width; var h = image.Height;
        var thickness = Math.Max(2f, w / 400f);
        var font = LabelFont.Value is { } f ? new Font(f.Family, Math.Max(11f, w / 60f), FontStyle.Bold) : null;

        image.Mutate(ctx =>
        {
            foreach (var d in detections)
            {
                var colour = ColourFor(d.ClassName);
                var x = d.X1 * w; var y = d.Y1 * h;
                var bw = Math.Max(1f, (d.X2 - d.X1) * w); var bh = Math.Max(1f, (d.Y2 - d.Y1) * h);
                ctx.Draw(colour, thickness, new RectangleF(x, y, bw, bh));

                if (font is null) continue;
                var label = $"{d.ClassName} {d.Confidence * 100:0}%";
                var size = TextMeasurer.MeasureSize(label, new TextOptions(font));
                var ly = y - size.Height - 4 < 0 ? y + 2 : y - size.Height - 4;
                ctx.Fill(colour, new RectangleF(x, ly, size.Width + 6, size.Height + 4));
                ctx.DrawText(label, font, Color.Black, new PointF(x + 3, ly + 2));
            }

            if (caption is not null && font is not null)
            {
                var size = TextMeasurer.MeasureSize(caption, new TextOptions(font));
                ctx.Fill(Color.FromRgba(0, 0, 0, 160), new RectangleF(0, h - size.Height - 8, size.Width + 12, size.Height + 8));
                ctx.DrawText(caption, font, Color.White, new PointF(6, h - size.Height - 4));
            }
        });
    }

    public static byte[] ToJpeg(Image image)
    {
        using var ms = new MemoryStream();
        image.Save(ms, Encoder);
        return ms.ToArray();
    }

    /// <summary>Annotates a frame straight from the detector.</summary>
    public static byte[] Annotate(Frame frame, Letterbox letterbox, IEnumerable<Detection> detections, string? caption = null)
    {
        using var image = FromFrame(frame, letterbox);
        Draw(image, detections, caption);
        return ToJpeg(image);
    }

    /// <summary>Annotates an existing JPEG, for archived and seeked frames.</summary>
    public static byte[] Annotate(byte[] jpeg, IEnumerable<Detection> detections, string? caption = null)
    {
        using var image = Image.Load<Rgb24>(jpeg);
        Draw(image, detections, caption);
        return ToJpeg(image);
    }

    private static Font? LoadFont()
    {
        foreach (var name in new[] { "Segoe UI", "Arial", "DejaVu Sans", "Liberation Sans", "Helvetica" })
            if (SystemFonts.TryGet(name, out var family)) return family.CreateFont(12, FontStyle.Bold);
        var any = SystemFonts.Families.FirstOrDefault();
        return any.Name is { Length: > 0 } ? any.CreateFont(12, FontStyle.Bold) : null;
    }
}
