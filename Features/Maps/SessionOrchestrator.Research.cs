namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// Converts one high-level alignment result into the stable research
    /// schema.  Callers invoke this only after the route has produced its
    /// final attempt, so OpenCV retries and intermediate candidates do not
    /// become separate research samples.
    /// </summary>
    private void RecordResearchAttempt(
        MapRecord map,
        string floorKey,
        CapturedGameFrame frame,
        MapRecognitionAttempt attempt,
        string floorSource,
        MapOverlayTransform? scaleSeed = null,
        IReadOnlyList<double>? searchRadii = null,
        int stableConfirmationFrames = 0,
        int stableConfirmationRequiredFrames = 0,
        bool calibrationUpdated = false,
        string? calibrationRejectionReason = null,
        RuntimeMapRecognition? recognitionOverride = null)
    {
        if (!_researchCollector.IsEnabled || _settings is not { } settings)
            return;

        _researchCollector.RecordAttempt(
            MapAlignmentResearchAttemptFactory.Create(
                map,
                floorKey,
                attempt,
                settings,
                SessionSnapshot,
                CreateWindowSignature(frame),
                floorSource,
                scaleSeed,
                searchRadii,
                stableConfirmationFrames,
                stableConfirmationRequiredFrames,
                calibrationUpdated,
                calibrationRejectionReason,
                recognitionOverride),
            map,
            floorKey,
            frame.Image);
    }

    private void RecordResearchAttemptForMap(
        MapRecord? map,
        string? floorKey,
        CapturedGameFrame frame,
        MapRecognitionAttempt attempt,
        string floorSource)
    {
        if (map is null)
            return;
        RecordResearchAttempt(
            map,
            floorKey
                ?? attempt.Recognition?.Result.Floor
                ?? MapFloorRules.GetPrimaryFloorKey(map),
            frame,
            attempt,
            floorSource);
    }

    private static MapWindowSignature CreateWindowSignature(CapturedGameFrame frame) =>
        new()
        {
            WindowHandle = frame.WindowHandle.ToInt64(),
            ClientX = (int)Math.Round(frame.ClientBounds.X),
            ClientY = (int)Math.Round(frame.ClientBounds.Y),
            ClientWidth = Math.Max(0, (int)Math.Round(frame.ClientBounds.Width)),
            ClientHeight = Math.Max(0, (int)Math.Round(frame.ClientBounds.Height)),
            ViewportX = (int)Math.Round(frame.ViewportBounds.X),
            ViewportY = (int)Math.Round(frame.ViewportBounds.Y),
            ViewportWidth = Math.Max(0, (int)Math.Round(frame.ViewportBounds.Width)),
            ViewportHeight = Math.Max(0, (int)Math.Round(frame.ViewportBounds.Height)),
            Dpi = DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle)
        };
}
/*
 * 文件职责：SessionOrchestrator.Research。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
