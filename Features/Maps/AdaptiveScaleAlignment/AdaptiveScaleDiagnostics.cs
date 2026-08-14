namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal static class AdaptiveScaleDiagnostics
{
    public static Dictionary<string, object?> State(
        AdaptiveScaleKey key,
        AdaptiveScaleController controller,
        string action,
        double? candidateScale = null) => new()
        {
            ["adaptiveAction"] = action,
            ["mapId"] = key.MapId,
            ["mapUpdatedAtTicks"] = key.MapUpdatedAtTicks,
            ["floor"] = key.FloorKey,
            ["clientWidth"] = key.ClientWidth,
            ["clientHeight"] = key.ClientHeight,
            ["viewportWidth"] = key.ViewportWidth,
            ["viewportHeight"] = key.ViewportHeight,
            ["state"] = controller.State.ToString(),
            ["runtimeScale"] = controller.RuntimeScale,
            ["calibrationScale"] = controller.CalibrationScale,
            ["hasRuntimeZoom"] = controller.HasRuntimeZoom,
            ["candidateScale"] = candidateScale
        };
}
