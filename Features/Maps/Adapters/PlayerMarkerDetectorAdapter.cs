using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IPlayerMarkerDetector 适配器 — 委托给 MapPlayerMarkerDetector。</summary>
public sealed class PlayerMarkerDetectorAdapter : IPlayerMarkerDetector
{
    private readonly MapPlayerMarkerDetector _detector = new();

    public object Detect(object liveViewport, object viewportBounds, object clientBounds,
        object playerSlot, string templatePath, object? previousPoint, object? tuning = null)
    {
        return _detector.Detect(
            (OpenCvSharp.Mat)liveViewport,
            (MapScreenRect)viewportBounds,
            (MapScreenRect)clientBounds,
            (PlayerSlot)playerSlot,
            templatePath,
            (MapViewportPoint?)previousPoint,
            (MapPlayerTrackingTuning?)tuning);
    }

    public void ResetTracking() => _detector.ResetTracking();
    public void Dispose() => _detector.Dispose();
}
