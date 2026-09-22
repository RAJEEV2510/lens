namespace Lens.Core.Inference;

/// <summary>
/// Aspect-preserving resize of a WxH frame into a square model input, padded on the short side.
/// Holds the numbers needed to map model-space boxes back to the original frame.
/// </summary>
public readonly record struct Letterbox(int SourceWidth, int SourceHeight, int TargetSize)
{
    public double Scale => Math.Min((double)TargetSize / SourceWidth, (double)TargetSize / SourceHeight);
    public int ScaledWidth => Math.Max(1, (int)Math.Round(SourceWidth * Scale));
    public int ScaledHeight => Math.Max(1, (int)Math.Round(SourceHeight * Scale));
    public int PadX => (TargetSize - ScaledWidth) / 2;
    public int PadY => (TargetSize - ScaledHeight) / 2;

    /// <summary>Model-space pixel box to normalised 0..1 source coordinates, clamped.</summary>
    public (float x1, float y1, float x2, float y2) ToNormalised(float mx1, float my1, float mx2, float my2)
    {
        var s = Scale;
        var x1 = (mx1 - PadX) / s / SourceWidth;
        var y1 = (my1 - PadY) / s / SourceHeight;
        var x2 = (mx2 - PadX) / s / SourceWidth;
        var y2 = (my2 - PadY) / s / SourceHeight;
        return (Clamp(x1), Clamp(y1), Clamp(x2), Clamp(y2));
    }

    private static float Clamp(double v) => (float)Math.Clamp(v, 0d, 1d);
}
