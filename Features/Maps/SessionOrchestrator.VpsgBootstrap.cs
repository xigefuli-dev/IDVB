namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// VPSG 缩放引导阶段：用 AKAZE 描述符几何独立估算本楼层 scale（不信任
    /// 跨楼层 seed），再做固定 scale 结构验证。成功短路返回；失败返回 null，
    /// 由调用方继续现有回退链。
    /// </summary>
    private MapRecognitionAttempt? TryAlignFloorWithVpsg(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence)
    {
        var deadline = NoDoorAlignmentDeadline.Current;
        if (deadline is not null
            && !deadline.CanStartStage(
                MapOpenAlignmentRouteRules.MinimumVpsgStageBudgetMilliseconds))
        {
            return null;
        }

        if (!TryCreateNoDoorStageTuning(
                structureTuning,
                out var vpsgTuning,
                maximumStageMilliseconds:
                    MapOpenAlignmentRouteRules.VpsgStageBudgetMilliseconds))
        {
            return null;
        }

        var attempt = _recognition.AlignLockedFloorFeature(
            frame,
            locked.Map.Id,
            floorKey,
            scaleSeed,
            alignmentMode,
            tuning,
            vpsgTuning,
            identityPriorConfidence);
        LogNoDoorStage(
            "vpsg-scale-bootstrap",
            attempt.Recognition is not null,
            attempt,
            attempt.Diagnostics.TotalMilliseconds,
            new Dictionary<string, object?>
            {
                ["scale"] = attempt.Diagnostics.ScaleBootstrapScale,
                ["scaleBootstrapSucceeded"] =
                    attempt.Diagnostics.ScaleBootstrapSucceeded,
                ["scaleBootstrapValidated"] =
                    attempt.Diagnostics.ScaleBootstrapValidated,
                ["uniqueMatches"] =
                    attempt.Diagnostics.ScaleBootstrapUniqueMatches,
                ["pairVotes"] = attempt.Diagnostics.ScaleBootstrapPairVotes,
                ["residualPx"] =
                    attempt.Diagnostics.ScaleBootstrapResidualPixels,
                ["relativeMad"] =
                    attempt.Diagnostics.ScaleBootstrapRelativeMad
            });
        return attempt;
    }
}
