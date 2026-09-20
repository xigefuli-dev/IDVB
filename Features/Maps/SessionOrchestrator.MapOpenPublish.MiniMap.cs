using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void PublishMiniMapAfterMainPresent(
        RuntimeMapRecognition recognition,
        string floorKey,
        bool failedAlignment)
    {
        var miniMapPublish = ActiveOperationTrace?.StartChild(
            "mini_map_publish",
            MapOperationWaitKind.Compute,
            mapId: recognition.Map.Id.ToString("D"),
            floorKey: floorKey);
        try
        {
            RefreshMiniMapForCurrentFloor();
            miniMapPublish?.Complete();
        }
        catch (Exception exception)
        {
            miniMapPublish?.Complete(
                MapOperationSpanStatus.Failed,
                exception.GetType().Name);
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                (failedAlignment
                    ? "失败状态已呈现"
                    : "主地图已呈现")
                + $"，但小地图刷新失败：{exception.Message}",
                details: new()
                {
                    ["exceptionType"] = exception.GetType().FullName,
                    ["stackTrace"] = exception.ToString()
                });
        }
    }
}
