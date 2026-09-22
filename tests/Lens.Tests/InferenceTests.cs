using Lens.Core.Inference;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lens.Tests;

public class LetterboxTests
{
    [Fact]
    public void Landscape_frame_is_padded_top_and_bottom()
    {
        var lb = new Letterbox(1600, 1200, 640);
        Assert.Equal(0.4, lb.Scale, 6);
        Assert.Equal(640, lb.ScaledWidth);
        Assert.Equal(480, lb.ScaledHeight);
        Assert.Equal(0, lb.PadX);
        Assert.Equal(80, lb.PadY);
    }

    [Fact]
    public void Portrait_frame_is_padded_left_and_right()
    {
        var lb = new Letterbox(720, 1280, 640);
        Assert.Equal(360, lb.ScaledWidth);
        Assert.Equal(640, lb.ScaledHeight);
        Assert.Equal(140, lb.PadX);
        Assert.Equal(0, lb.PadY);
    }

    [Fact]
    public void Model_box_maps_back_to_normalised_source_coordinates()
    {
        var lb = new Letterbox(1600, 1200, 640);
        // A box covering the whole padded image content maps to the full source frame.
        var (x1, y1, x2, y2) = lb.ToNormalised(0, 80, 640, 560);
        Assert.Equal(0f, x1, 3);
        Assert.Equal(0f, y1, 3);
        Assert.Equal(1f, x2, 3);
        Assert.Equal(1f, y2, 3);

        // The centre stays the centre.
        var (cx1, cy1, cx2, cy2) = lb.ToNormalised(310, 310, 330, 330);
        Assert.Equal(0.5f, (cx1 + cx2) / 2, 2);
        Assert.Equal(0.5f, (cy1 + cy2) / 2, 2);
    }

    [Fact]
    public void Boxes_in_the_padding_are_clamped()
    {
        var lb = new Letterbox(1600, 1200, 640);
        var (_, y1, _, y2) = lb.ToNormalised(0, 0, 640, 640);
        Assert.Equal(0f, y1);
        Assert.Equal(1f, y2);
    }
}

public class NmsTests
{
    [Fact]
    public void Overlapping_boxes_of_the_same_class_keep_only_the_best()
    {
        var boxes = new[]
        {
            new RawBox(10, 10, 110, 110, 0.9f, 2),
            new RawBox(12, 12, 112, 112, 0.8f, 2),
            new RawBox(15, 15, 115, 115, 0.7f, 2),
        };
        var kept = Nms.Apply(boxes, 0.5f);
        Assert.Single(kept);
        Assert.Equal(0.9f, kept[0].Score);
    }

    [Fact]
    public void Overlapping_boxes_of_different_classes_are_both_kept()
    {
        var boxes = new[]
        {
            new RawBox(10, 10, 110, 110, 0.9f, 2),
            new RawBox(12, 12, 112, 112, 0.8f, 7),
        };
        Assert.Equal(2, Nms.Apply(boxes, 0.5f).Count);
    }

    [Fact]
    public void Distant_boxes_are_both_kept()
    {
        var boxes = new[]
        {
            new RawBox(0, 0, 50, 50, 0.9f, 0),
            new RawBox(200, 200, 250, 250, 0.5f, 0),
        };
        Assert.Equal(2, Nms.Apply(boxes, 0.5f).Count);
    }

    [Fact]
    public void IoU_of_identical_boxes_is_one_and_of_disjoint_boxes_is_zero()
    {
        var a = new RawBox(0, 0, 10, 10, 1, 0);
        Assert.Equal(1f, a.IoU(a), 5);
        Assert.Equal(0f, a.IoU(new RawBox(20, 20, 30, 30, 1, 0)), 5);
    }
}

public class YoloPostprocessTests
{
    [Fact]
    public void Parses_yolov10_end_to_end_output_and_applies_threshold_and_class_filter()
    {
        // [1, N, 6] rows: x1, y1, x2, y2, score, class
        var t = new DenseTensor<float>([1, 3, 6]);
        Set(t, 0, [10, 10, 50, 50, 0.9f, 2]);
        Set(t, 1, [60, 60, 90, 90, 0.2f, 2]);   // below threshold
        Set(t, 2, [100, 100, 150, 150, 0.8f, 0]);

        var all = YoloPostprocess.Parse(t, 0.35f, 0.5f, null);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, b => b.ClassId == 2 && b.Score == 0.9f);
        Assert.Contains(all, b => b.ClassId == 0);

        var carsOnly = YoloPostprocess.Parse(t, 0.35f, 0.5f, [2]);
        Assert.Single(carsOnly);
        Assert.Equal(2, carsOnly[0].ClassId);
    }

    [Fact]
    public void Parses_yolov8_anchor_output_with_xywh_and_nms()
    {
        // [1, 4 + C, A] with C = 3 classes and A = 3 anchors. Two anchors describe the same object.
        var t = new DenseTensor<float>([1, 7, 3]);
        // anchor 0: centre (100,100) size 40x40, class 1 score 0.9
        t[0, 0, 0] = 100; t[0, 1, 0] = 100; t[0, 2, 0] = 40; t[0, 3, 0] = 40; t[0, 5, 0] = 0.9f;
        // anchor 1: nearly identical, lower score -> suppressed
        t[0, 0, 1] = 102; t[0, 1, 1] = 101; t[0, 2, 1] = 40; t[0, 3, 1] = 40; t[0, 5, 1] = 0.7f;
        // anchor 2: far away, class 2
        t[0, 0, 2] = 300; t[0, 1, 2] = 300; t[0, 2, 2] = 20; t[0, 3, 2] = 20; t[0, 6, 2] = 0.6f;

        var boxes = YoloPostprocess.Parse(t, 0.35f, 0.5f, null);
        Assert.Equal(2, boxes.Count);
        var best = boxes.Single(b => b.ClassId == 1);
        Assert.Equal(80, best.X1); Assert.Equal(80, best.Y1); Assert.Equal(120, best.X2); Assert.Equal(120, best.Y2);
        Assert.Equal(0.9f, best.Score);
    }

    [Fact]
    public void Rejects_unknown_output_shapes()
    {
        var t = new DenseTensor<float>([2, 6]);
        Assert.Throws<NotSupportedException>(() => YoloPostprocess.Parse(t, 0.35f, 0.5f, null));
    }

    private static void Set(DenseTensor<float> t, int row, float[] values)
    {
        for (var i = 0; i < values.Length; i++) t[0, row, i] = values[i];
    }
}
