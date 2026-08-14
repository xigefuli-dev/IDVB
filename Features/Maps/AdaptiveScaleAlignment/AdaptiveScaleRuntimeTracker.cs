namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed class AdaptiveScaleRuntimeTracker
{
    public double? CalibrationScale { get; private set; }
    public double RuntimeScale { get; private set; }
    public bool HasRuntimeZoom { get; private set; }

    public void Begin(double initialScale, double? calibrationScale)
    {
        RuntimeScale = initialScale;
        CalibrationScale = calibrationScale;
        HasRuntimeZoom = false;
    }

    public void SetCalibration(double scale) => CalibrationScale = scale;

    public void SetRuntime(double scale, bool isRuntimeZoom)
    {
        RuntimeScale = scale;
        HasRuntimeZoom |= isRuntimeZoom;
    }

    public void EndOpen()
    {
        RuntimeScale = 0d;
        CalibrationScale = null;
        HasRuntimeZoom = false;
    }
}
