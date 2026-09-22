namespace Lens.Core.Inference;

/// <summary>Raw model output box in model pixel space, before mapping back to the source frame.</summary>
public readonly record struct RawBox(float X1, float Y1, float X2, float Y2, float Score, int ClassId)
{
    public float Area => Math.Max(0, X2 - X1) * Math.Max(0, Y2 - Y1);

    public float IoU(in RawBox other)
    {
        var ix1 = Math.Max(X1, other.X1);
        var iy1 = Math.Max(Y1, other.Y1);
        var ix2 = Math.Min(X2, other.X2);
        var iy2 = Math.Min(Y2, other.Y2);
        var inter = Math.Max(0, ix2 - ix1) * Math.Max(0, iy2 - iy1);
        var union = Area + other.Area - inter;
        return union <= 0 ? 0 : inter / union;
    }
}

public static class Nms
{
    /// <summary>Greedy per-class non-maximum suppression. Input need not be sorted.</summary>
    public static List<RawBox> Apply(IEnumerable<RawBox> boxes, float iouThreshold)
    {
        var kept = new List<RawBox>();
        foreach (var group in boxes.GroupBy(b => b.ClassId))
        {
            var sorted = group.OrderByDescending(b => b.Score).ToList();
            var suppressed = new bool[sorted.Count];
            for (var i = 0; i < sorted.Count; i++)
            {
                if (suppressed[i]) continue;
                kept.Add(sorted[i]);
                for (var j = i + 1; j < sorted.Count; j++)
                {
                    if (!suppressed[j] && sorted[i].IoU(sorted[j]) > iouThreshold) suppressed[j] = true;
                }
            }
        }
        return kept;
    }
}
