using Lens.Core.Models;
using Lens.Core.Video;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Lens.Core.Inference;

public sealed record DetectorOptions
{
    public required string ModelPath { get; init; }
    public int InputSize { get; init; } = 640;
    public float ConfidenceThreshold { get; init; } = 0.35f;
    public float IouThreshold { get; init; } = 0.5f;
    /// <summary>COCO class ids to keep. Null keeps everything.</summary>
    public HashSet<int>? KeepClasses { get; init; }
}

/// <summary>
/// Runs a YOLO ONNX model on CPU. Supports the YOLOv10 end-to-end head (1x300x6, no NMS needed)
/// and the classic YOLOv8 head (1x84x8400, NMS applied here). The format is detected from the output shape.
/// </summary>
public sealed class YoloDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly DetectorOptions _options;
    private readonly float[] _inputBuffer;

    public YoloDetector(DetectorOptions options)
    {
        _options = options;
        if (!File.Exists(options.ModelPath))
            throw new FileNotFoundException($"ONNX model not found at {options.ModelPath}. Run scripts/get-models.ps1.", options.ModelPath);

        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(options.ModelPath, so);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();
        _inputBuffer = new float[3 * options.InputSize * options.InputSize];
    }

    public int InputSize => _options.InputSize;

    /// <summary>Runs the model on one letterboxed RGB24 frame and returns boxes in model pixel space.</summary>
    public List<RawBox> DetectRaw(ReadOnlySpan<byte> rgb)
    {
        var size = _options.InputSize;
        var plane = size * size;
        if (rgb.Length != plane * 3)
            throw new ArgumentException($"Expected {plane * 3} RGB bytes, got {rgb.Length}", nameof(rgb));

        // HWC uint8 -> CHW float32 in 0..1
        for (var i = 0; i < plane; i++)
        {
            var o = i * 3;
            _inputBuffer[i] = rgb[o] / 255f;
            _inputBuffer[plane + i] = rgb[o + 1] / 255f;
            _inputBuffer[2 * plane + i] = rgb[o + 2] / 255f;
        }

        var tensor = new DenseTensor<float>(_inputBuffer, [1, 3, size, size]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_inputName, tensor)]);
        var output = results.First(r => r.Name == _outputName).AsTensor<float>();
        return YoloPostprocess.Parse(output, _options.ConfidenceThreshold, _options.IouThreshold, _options.KeepClasses);
    }

    /// <summary>Full pipeline for one frame: inference, then map boxes back to normalised source coordinates.</summary>
    public List<Detection> Detect(Frame frame, Letterbox letterbox, int videoId)
    {
        var raw = DetectRaw(frame.Rgb);
        var list = new List<Detection>(raw.Count);
        foreach (var b in raw)
        {
            var (x1, y1, x2, y2) = letterbox.ToNormalised(b.X1, b.Y1, b.X2, b.Y2);
            if (x2 - x1 <= 0 || y2 - y1 <= 0) continue;
            list.Add(new Detection(videoId, frame.Index, frame.TimestampSeconds, b.ClassId, CocoLabels.Name(b.ClassId), b.Score, x1, y1, x2, y2));
        }
        return list;
    }

    public void Dispose() => _session.Dispose();
}

public static class YoloPostprocess
{
    public static List<RawBox> Parse(Tensor<float> output, float confThreshold, float iouThreshold, HashSet<int>? keepClasses)
    {
        var dims = output.Dimensions;
        if (dims.Length != 3 || dims[0] != 1)
            throw new NotSupportedException($"Unexpected YOLO output shape [{string.Join(",", dims.ToArray())}]");

        // YOLOv10 / end-to-end: [1, N, 6] = x1,y1,x2,y2,score,class. Already NMS-free.
        if (dims[2] == 6)
        {
            var boxes = new List<RawBox>();
            for (var i = 0; i < dims[1]; i++)
            {
                var score = output[0, i, 4];
                if (score < confThreshold) continue;
                var cls = (int)output[0, i, 5];
                if (keepClasses is not null && !keepClasses.Contains(cls)) continue;
                boxes.Add(new RawBox(output[0, i, 0], output[0, i, 1], output[0, i, 2], output[0, i, 3], score, cls));
            }
            return boxes;
        }

        // YOLOv8 / v11: [1, 4 + C, A] = cx,cy,w,h then C class scores, per anchor. Needs NMS.
        var numClasses = dims[1] - 4;
        var anchors = dims[2];
        var candidates = new List<RawBox>();
        for (var a = 0; a < anchors; a++)
        {
            var best = -1;
            var bestScore = 0f;
            for (var c = 0; c < numClasses; c++)
            {
                var s = output[0, 4 + c, a];
                if (s > bestScore) { bestScore = s; best = c; }
            }
            if (bestScore < confThreshold) continue;
            if (keepClasses is not null && !keepClasses.Contains(best)) continue;

            var cx = output[0, 0, a];
            var cy = output[0, 1, a];
            var w = output[0, 2, a];
            var h = output[0, 3, a];
            candidates.Add(new RawBox(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2, bestScore, best));
        }
        return Nms.Apply(candidates, iouThreshold);
    }
}
