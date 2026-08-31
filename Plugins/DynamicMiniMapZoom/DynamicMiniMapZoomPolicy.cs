namespace IDVBuff.Plugins.DynamicMiniMapZoom;

/// <summary>
/// 动态小地图缩放的纯策略。只负责把滚轮刻度转换为临时显示尺度，
/// 不读写用户设置，也不保存任何对局状态。
/// </summary>
public static class DynamicMiniMapZoomPolicy
{
    public const double MinimumScale = 0.10d;
    public const double MaximumScale = 1.0d;
    public const double ScalePerWheelNotch = 0.02d;
    public const double MinimumSensitivityPercent = 25d;
    public const double MaximumSensitivityPercent = 300d;
    public const double DefaultSensitivityPercent = 50d;
    public const int StandardWheelDelta = 120;

    public static double Apply(
        double currentScale,
        int wheelDelta,
        double sensitivityPercent = DefaultSensitivityPercent)
    {
        if (!double.IsFinite(currentScale))
            currentScale = MinimumScale;

        sensitivityPercent = double.IsFinite(sensitivityPercent)
            ? Math.Clamp(
                sensitivityPercent,
                MinimumSensitivityPercent,
                MaximumSensitivityPercent)
            : DefaultSensitivityPercent;
        var notches = wheelDelta / (double)StandardWheelDelta;
        var nextScale = currentScale
            + notches * ScalePerWheelNotch * sensitivityPercent / 100d;
        return Math.Clamp(nextScale, MinimumScale, MaximumScale);
    }
}
