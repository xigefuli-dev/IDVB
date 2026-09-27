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

    public bool Matches(CapturedGameFrame frame) => _image is not null
        && _window == frame.WindowHandle && _clientBounds == frame.ClientBounds
        && _viewport == frame.ViewportBounds && _image.Size() == frame.Image.Size()
        && _image.Type() == frame.Image.Type()
        && Cv2.Norm(_image, frame.Image, NormTypes.INF) == 0;

    public void Remember(CapturedGameFrame frame)
    {
        Reset();
        _image = frame.Image.Clone();
        _clientBounds = frame.ClientBounds;
        _viewport = frame.ViewportBounds;
        _window = frame.WindowHandle;
    }

    public void Reset()
    {
        _image?.Dispose();
        _image = null;
    }

    public void Dispose() => Reset();
}
