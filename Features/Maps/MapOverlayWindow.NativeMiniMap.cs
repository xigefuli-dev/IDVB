namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    private double? _nativeMiniMapHeading;
    private MapOverlayTransform? _miniMapHeadingTransform;

    public void SetNativeMiniMapHeading(double? degrees)
    {
        if (_disposed || degrees is { } value && !double.IsFinite(value)) return;
        if (_nativeMiniMapHeading is { } previous && degrees is { } next
            && Math.Abs(NativeMiniMapHeadingDetector.ShortestDelta(previous, next)) < .25) return;
        if (_nativeMiniMapHeading == degrees) return;
        _nativeMiniMapHeading = degrees;
        if (_persistentMiniMap is not null && IsVisible) Present();
    }

    private float? ResolveMiniMapRotation()
    {
        if (_nativeMiniMapHeading is not { } heading) return null;
        var transform = _miniMapHeadingTransform;
        // Native and full game maps share north. Convert through this floor's own alignment.
        var radians = (heading - 90 - (transform?.OrientationDegrees ?? 0)) * Math.PI / 180;
        var sx = transform?.ScaleX is > 0 ? transform.ScaleX : 1;
        var sy = transform?.ScaleY is > 0 ? transform.ScaleY : 1;
        var referenceHeading = Math.Atan2(Math.Sin(radians) / sy, Math.Cos(radians) / sx) * 180 / Math.PI + 90;
        return (float)-referenceHeading;
    }
}
