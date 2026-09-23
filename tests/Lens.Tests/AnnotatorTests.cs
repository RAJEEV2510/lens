using Lens.Core.Indexing;
using Lens.Core.Inference;
using Lens.Core.Models;
using Lens.Core.Video;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Lens.Tests;

/// <summary>Server-side annotation: boxes land where the normalised coordinates say, on the detector's own frame.</summary>
public class AnnotatorTests
{
    [Fact]
    public void Box_is_drawn_at_the_normalised_position_in_the_class_colour()
    {
        // A 640x640 letterboxed input for a 320x180 source: grey everywhere.
        var letterbox = new Letterbox(320, 180, 640);
        var rgb = new byte[640 * 640 * 3];
        Array.Fill(rgb, (byte)128);
        var frame = new Frame(rgb, 7, 3.5);
        var det = new Detection(1, 7, 3.5, 2, "car", 0.9f, 0.25f, 0.25f, 0.75f, 0.75f);

        // Geometry on the raw image (JPEG would blur a two-pixel line), then the JPEG path just has to round-trip.
        using var image = FrameAnnotator.FromFrame(frame, letterbox);
        FrameAnnotator.Draw(image, [det]);
        Assert.Equal(letterbox.ScaledWidth, image.Width);      // cropped back to the source aspect ratio
        Assert.Equal(letterbox.ScaledHeight, image.Height);

        var car = FrameAnnotator.ColourFor("car").ToPixel<Rgb24>();
        var onLeftEdge = image[(int)(0.25f * image.Width), image.Height / 2];
        var inside = image[image.Width / 2, (int)(0.6f * image.Height)];
        Assert.True(Close(onLeftEdge, car), $"edge pixel {onLeftEdge} should be car green {car}");
        Assert.True(Close(inside, new Rgb24(128, 128, 128)), $"interior pixel {inside} should be untouched grey");

        using var decoded = Image.Load<Rgb24>(FrameAnnotator.Annotate(frame, letterbox, [det], "caption"));
        Assert.Equal(image.Width, decoded.Width);
        Assert.Equal(image.Height, decoded.Height);
    }

    [Fact]
    public void Existing_jpeg_can_be_annotated_too()
    {
        using var src = new Image<Rgb24>(200, 100, new Rgb24(10, 10, 10));
        using var ms = new MemoryStream();
        src.SaveAsJpeg(ms);
        var det = new Detection(1, 0, 0, 0, "person", 0.5f, 0.1f, 0.1f, 0.4f, 0.9f);

        using var image = Image.Load<Rgb24>(FrameAnnotator.Annotate(ms.ToArray(), [det], "caption"));
        Assert.Equal(200, image.Width);
        Assert.Equal(100, image.Height);
        // Somewhere along the left edge of the box a pixel must carry the class colour, JPEG blur allowed for.
        var person = FrameAnnotator.ColourFor("person").ToPixel<Rgb24>();
        var found = Enumerable.Range(18, 6).Any(x => Close(image[x, 50], person, 70));
        Assert.True(found, "no person-red pixel near x=20");
    }

    private static bool Close(Rgb24 a, Rgb24 b, int tol = 40) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
}
