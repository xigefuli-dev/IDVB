namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// VPSG 缩放引导阶段：用本楼层边缘结构独立估算 scale（不信任跨楼层
    /// seed），再做固定 scale 结构验证。成功短路返回；失败返回 null，
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
        if (MapAlignmentChannelRegistry.Resolve(
                locked.Map,
                floorKey).Channel == MapAlignmentChannel.LowStructure)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"低结构楼层拒绝 VPSG · floor={floorKey}");
            return null;
        }
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
                ["finalScale"] =
                    attempt.Recognition?.Result.OverlayTransform?.ScaleX,
                ["scaleBootstrapSucceeded"] =
                    attempt.Diagnostics.ScaleBootstrapSucceeded,
                ["scaleBootstrapValidated"] =
                    attempt.Diagnostics.ScaleBootstrapValidated,
                ["method"] = attempt.Diagnostics.ScaleBootstrapMethod,
                ["cost"] = attempt.Diagnostics.ScaleBootstrapCost,
                ["margin"] = attempt.Diagnostics.ScaleBootstrapMargin,
                ["hintScale"] = attempt.Diagnostics.ScaleBootstrapHintScale,
                ["hintConfidence"] =
                    attempt.Diagnostics.ScaleBootstrapHintConfidence,
                ["searchMinimumScale"] =
                    attempt.Diagnostics.ScaleBootstrapSearchMinimum,
                ["searchMaximumScale"] =
                    attempt.Diagnostics.ScaleBootstrapSearchMaximum,
                ["mode"] = attempt.Diagnostics.ScaleBootstrapMode,
                ["legacyScale"] =
                    attempt.Diagnostics.ScaleBootstrapLegacyScale,
                ["legacyConfidence"] =
                    attempt.Diagnostics.ScaleBootstrapLegacyConfidence,
                ["legacyMilliseconds"] =
                    attempt.Diagnostics.ScaleBootstrapLegacyMilliseconds,
                ["structureMilliseconds"] =
                    attempt.Diagnostics.ScaleBootstrapStructureMilliseconds,
                ["candidateCount"] =
                    attempt.Diagnostics.ScaleBootstrapCandidateCount,
                ["selectedCandidateIndex"] =
                    attempt.Diagnostics.ScaleBootstrapSelectedCandidateIndex,
                ["testedScaleCount"] =
                    attempt.Diagnostics.ScaleBootstrapTestedScaleCount,
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
/*
 * 文件职责：SessionOrchestrator.VpsgBootstrap。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
