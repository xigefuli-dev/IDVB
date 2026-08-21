namespace IDVBuff.Features.Maps;

public sealed partial class SideEntranceScanPipeline
{
    private static void ClassifyTemplateEvidence(
        SideEntranceScanCandidate candidate,
        GateDetection? detectedGate,
        MapScreenRect? viewportBounds)
    {
        if (candidate.MatchScore < SideEntranceScanRules.MinimumReferenceSimilarity)
        {
            Reject(candidate, SideEntranceRejectionReason.WeakTemplateSimilarity,
                $"模板相似度 {candidate.MatchScore:P1} 低于参考门槛。");
            return;
        }

        if (detectedGate is not null && viewportBounds is { IsValid: true } viewport)
        {
            candidate.GateSpatialResidualPixels = CalculateGateResidual(
                candidate, detectedGate, viewport);
            if (!double.IsFinite(candidate.GateSpatialResidualPixels)
                || candidate.GateSpatialResidualPixels
                    > SideEntranceScanRules.MaximumGateSpatialResidualPixels)
            {
                Reject(candidate, SideEntranceRejectionReason.GateSpatialMismatch,
                    $"模板门位置与检测门相差 {candidate.GateSpatialResidualPixels:F1}px。");
                return;
            }
        }

        var tolerance = SideEntranceScanRules.ScaleBoundaryTolerance;
        if (candidate.MatchScale <= SideEntranceScanRules.MinimumScale * (1d + tolerance)
            || candidate.MatchScale >= SideEntranceScanRules.MaximumScale * (1d - tolerance))
        {
            candidate.RejectionReason = SideEntranceRejectionReason.ScaleAtSearchBoundary;
            candidate.RejectionDetail = "最佳缩放落在搜索边界，不能作为可靠身份依据。";
            return;
        }

        if (candidate.TemplateMargin < SideEntranceScanRules.MinimumTemplateMargin)
        {
            candidate.RejectionReason = SideEntranceRejectionReason.AmbiguousTemplateRanking;
            candidate.RejectionDetail =
                $"与相邻模板仅相差 {candidate.TemplateMargin:P1}，身份不唯一。";
        }
    }

    private static double CalculateGateResidual(
        SideEntranceScanCandidate candidate,
        GateDetection gate,
        MapScreenRect viewport)
    {
        var profile = MapFloorRules.GetFloorProfile(candidate.Map, candidate.FloorKey);
        var anchor = profile?.FindAnchor("side-entrance");
        if (profile is null || anchor?.Bounds?.IsValid is not true)
            return double.PositiveInfinity;

        var anchorCenterX = (anchor.Bounds.X + anchor.Bounds.Width / 2d)
            * profile.RecognitionPixelWidth;
        var anchorCenterY = (anchor.Bounds.Y + anchor.Bounds.Height / 2d)
            * profile.RecognitionPixelHeight;
        var featureOriginX = profile.SideEntranceFeatureCenterX
            - (candidate.MatchLocation.Width / candidate.MatchScale / 2d);
        var featureOriginY = profile.SideEntranceFeatureCenterY
            - (candidate.MatchLocation.Height / candidate.MatchScale / 2d);
        var predictedX = candidate.MatchLocation.X
            + ((anchorCenterX - featureOriginX) * candidate.MatchScale);
        var predictedY = candidate.MatchLocation.Y
            + ((anchorCenterY - featureOriginY) * candidate.MatchScale);
        var detectedX = gate.ScreenBounds.CenterX - viewport.X;
        var detectedY = gate.ScreenBounds.CenterY - viewport.Y;
        return Math.Sqrt(
            Math.Pow(predictedX - detectedX, 2d)
            + Math.Pow(predictedY - detectedY, 2d));
    }

    private static void Reject(
        SideEntranceScanCandidate candidate,
        SideEntranceRejectionReason reason,
        string detail)
    {
        candidate.Disposition = SideEntranceCandidateDisposition.Rejected;
        candidate.RejectionReason = reason;
        candidate.RejectionDetail = detail;
    }
}
/*
 * 文件职责：SideEntranceScanPipeline.Evidence。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
