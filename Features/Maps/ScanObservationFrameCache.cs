using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>One exact frame, only to avoid re-comparing an unchanged ambiguous view.
/// This is not a confidence accumulator or a map/floor alignment cache.</summary>
internal sealed class ScanObservationFrameCache : IDisposable
{
    private Mat? _image;
    private MapScreenRect _clientBounds;
    private MapScreenRect _viewport;
    private IntPtr _window;
    private object? _catalogRevision;
    private ScanPerformanceMode _mode;
    private string? _floor;

    public bool Matches(CapturedGameFrame frame, object catalogRevision, ScanPerformanceMode mode) => _image is not null
        && Equals(_catalogRevision, catalogRevision) && _mode == mode
        && string.Equals(_floor, frame.DetectedFloorKey, StringComparison.OrdinalIgnoreCase)
        && _window == frame.WindowHandle && _clientBounds == frame.ClientBounds
        && _viewport == frame.ViewportBounds && _image.Size() == frame.Image.Size()
        && _image.Type() == frame.Image.Type()
        && Cv2.Norm(_image, frame.Image, NormTypes.INF) == 0;

    public void Remember(CapturedGameFrame frame, object catalogRevision, ScanPerformanceMode mode)
    {
        Reset();
        _image = frame.Image.Clone();
        _clientBounds = frame.ClientBounds;
        _viewport = frame.ViewportBounds;
        _window = frame.WindowHandle;
        _catalogRevision = catalogRevision;
        _mode = mode;
        _floor = frame.DetectedFloorKey;
    }

    public void Reset()
    {
        _image?.Dispose();
        _image = null;
        _catalogRevision = null;
    }

    public void Dispose() => Reset();
}
