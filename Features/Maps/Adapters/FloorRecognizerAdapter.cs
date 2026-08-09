using IDVBuff.Core.Contracts;
using OpenCvSharp;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IFloorRecognizer 适配器 — 委托给 FloorIndicatorRecognizer。</summary>
public sealed class FloorRecognizerAdapter : IFloorRecognizer
{
    private readonly FloorIndicatorRecognizer _recognizer;

    public FloorRecognizerAdapter(string firstFloorPath, string secondFloorPath)
    {
        _recognizer = new FloorIndicatorRecognizer(firstFloorPath, secondFloorPath);
    }

    public object Recognize(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride) =>
        _recognizer.Recognize(bgraPixels, width, height, stride);

    public object Recognize(ReadOnlySpan<byte> bgraPixels, int width, int height, int stride, object tuning) =>
        _recognizer.Recognize(bgraPixels, width, height, stride, (MapFloorRecognitionTuning?)tuning);

    public object Recognize(object image) =>
        _recognizer.Recognize((Mat)image);

    public object Recognize(object image, object tuning) =>
        _recognizer.Recognize((Mat)image, (MapFloorRecognitionTuning?)tuning);

    public void Dispose() => _recognizer.Dispose();
}
