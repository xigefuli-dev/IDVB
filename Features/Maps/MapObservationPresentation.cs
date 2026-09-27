using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

/// <summary>Owns only a provisional display, never an identity or trusted alignment.</summary>
internal sealed class MapObservationPresentation
{
    private MapScreenRect _clientBounds;
    private MapScreenRect _viewportBounds;
    private IntPtr _window;
    private string? _floor;

    public void Reset() => _floor = null;

    public bool RetainForTarget(IOverlayWindow overlay, MapScreenRect clientBounds,
        MapScreenRect viewportBounds, IntPtr window, string? detectedFloor = null)
    {
        if (_floor is null) return false;
        if (_clientBounds == clientBounds && _viewportBounds == viewportBounds && _window == window
            && (detectedFloor is null || string.Equals(_floor, detectedFloor, StringComparison.OrdinalIgnoreCase)))
            return true;

        // A changed capture target invalidates screen coordinates. New pixels or an
        // inconclusive comparison alone do not invalidate a provisional resource.
        Reset();
        using var present = overlay.DeferPresent();
        overlay.ClearMap();
        overlay.SetObservationRegion(null);
        return false;
    }

    public void Publish(IOverlayWindow overlay, RuntimeMapRecognition? replacement,
        CapturedGameFrame frame, bool showStatus)
    {
        if (replacement is null)
        {
            RetainForTarget(overlay, frame.ClientBounds, frame.ViewportBounds,
                frame.WindowHandle, frame.DetectedFloorKey);
            return;
        }

        // Replace in place: never present an empty map between two usable previews.
        using var present = overlay.DeferPresent();
        overlay.UpdateMap(replacement, frame.ClientBounds, frame.WindowHandle, showStatus, frame.ViewportBounds);
        _clientBounds = frame.ClientBounds;
        _viewportBounds = frame.ViewportBounds;
        _window = frame.WindowHandle;
        _floor = replacement.Result.Floor;
        overlay.SetObservationRegion(frame.ViewportBounds);
    }

    public static IDisposable SuspendForCapture(IOverlayWindow overlay, Func<bool> canRestore) =>
        new CaptureLease(overlay, canRestore);

    private sealed class CaptureLease : IDisposable
    {
        private IOverlayWindow? _overlay;
        private readonly Func<bool> _canRestore;

        public CaptureLease(IOverlayWindow overlay, Func<bool> canRestore)
        {
            _canRestore = canRestore;
            if (!overlay.IsVisible || overlay.IsCaptureExclusionEnabled) return;
            _overlay = overlay;
            // GDI captures the desktop, including our own map if it remains visible.
            overlay.Hide();
        }

        public void Dispose()
        {
            var overlay = Interlocked.Exchange(ref _overlay, null);
            if (overlay is not null && _canRestore()) overlay.Show();
        }
    }
}
