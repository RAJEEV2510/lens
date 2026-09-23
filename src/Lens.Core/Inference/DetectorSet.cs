using Lens.Core.Models;
using Lens.Core.Video;

namespace Lens.Core.Inference;

/// <summary>
/// The object model plus, when a face model is configured, the face model, run on the same letterboxed frame.
/// Faces come out as class <see cref="CocoLabels.Face"/> next to the COCO classes.
/// </summary>
public sealed class DetectorSet : IDisposable
{
    private readonly YoloDetector _objects;
    private readonly YoloDetector? _faces;

    public DetectorSet(string modelPath, string? faceModelPath, float confidence, HashSet<int>? keepClasses = null)
    {
        _objects = new YoloDetector(new DetectorOptions { ModelPath = modelPath, ConfidenceThreshold = confidence, KeepClasses = keepClasses });
        if (faceModelPath is not null && (keepClasses is null || keepClasses.Contains(CocoLabels.Face)))
        {
            _faces = new YoloDetector(new DetectorOptions { ModelPath = faceModelPath, ConfidenceThreshold = confidence, ClassOffset = CocoLabels.Face });
            if (_faces.InputSize != _objects.InputSize)
                throw new InvalidOperationException("the face model must use the same input size as the object model");
        }
    }

    public int InputSize => _objects.InputSize;

    public List<Detection> Detect(Frame frame, Letterbox letterbox, int videoId)
    {
        var found = _objects.Detect(frame, letterbox, videoId);
        if (_faces is not null) found.AddRange(_faces.Detect(frame, letterbox, videoId));
        return found;
    }

    public void Dispose()
    {
        _objects.Dispose();
        _faces?.Dispose();
    }
}
