namespace Lens.Core.Models;

/// <summary>One detected object in one sampled frame. Box coordinates are normalised to 0..1 of the original video size.</summary>
public sealed record Detection(
    int VideoId,
    int FrameIndex,
    double TimestampSeconds,
    int ClassId,
    string ClassName,
    float Confidence,
    float X1,
    float Y1,
    float X2,
    float Y2)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
}
