namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    internal static double CalculateGateResidual(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewport)
    {
        var profile = MapFloorRules.GetFloorProfile(candidate.Map, candidate.FloorKey);
        var anchor = MapScanFloorRules.GetScanFeatureAnchor(
            candidate.Map,
            candidate.FloorKey);
        if (profile is null || anchor?.Bounds?.IsValid is not true)
            return double.PositiveInfinity;

        var anchorCenterX = (anchor.Bounds.X + anchor.Bounds.Width / 2d)
            * profile.RecognitionPixelWidth;
        var anchorCenterY = (anchor.Bounds.Y + anchor.Bounds.Height / 2d)
            * profile.RecognitionPixelHeight;
        // MatchLocation is the full prebuilt layer origin; no cropped-feature offset exists.
        var predictedX = candidate.MatchLocation.X + anchorCenterX * candidate.MatchScale;
        var predictedY = candidate.MatchLocation.Y + anchorCenterY * candidate.MatchScale;
        var detectedX = gate.ScreenBounds.CenterX - viewport.X;
        var detectedY = gate.ScreenBounds.CenterY - viewport.Y;
        return Math.Sqrt(
            Math.Pow(predictedX - detectedX, 2d)
            + Math.Pow(predictedY - detectedY, 2d));
    }

}
/*
 * 文件职责：SideEntranceScanPipeline.Evidence。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
