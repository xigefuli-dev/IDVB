namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt? ApplyNoDoorBudgetBeforeLocalSearch(
        MapStructureRegistrationTuning tuning,
        bool isSideEntranceStructureRoute,
        MapScanDiagnostics diagnostics,
        GateDetectionResult? gateResult)
    {
        if (tuning.Channel == MapAlignmentChannel.LowStructure)
            return null;
        if (tuning.Mode == MapStructureRegistrationMode.ScanVerification)
        {
            tuning.StructureFallbackBudgetMilliseconds = Math.Min(
                tuning.StructureFallbackBudgetMilliseconds,
                MapOpenAlignmentRouteRules
                    .ScanVerificationFormalStructureBudgetMilliseconds);
            return null;
        }
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
                is not { } remaining)
        {
            return null;
        }
        if (remaining < MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds)
        {
            const string reason =
                "无门对齐预处理后已无足够的结构搜索预算，请保持地图打开并重试。";
            var timedOut = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.TimeBudgetExceeded,
                reason);
            return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                diagnostics,
                timedOut,
                reason,
                gateResult,
                AlignmentSearchStage.StructureFallback);
        }

        tuning.StructureFallbackBudgetMilliseconds = Math.Min(
            tuning.StructureFallbackBudgetMilliseconds,
            isSideEntranceStructureRoute
                ? Math.Min(500, remaining)
                : remaining);
        return null;
    }

    private static bool TryApplyNoDoorBudgetBeforeGlobalSearch(
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrationResult localResult)
    {
        if (tuning.Channel == MapAlignmentChannel.LowStructure)
            return true;
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
                is not { } remaining)
        {
            return true;
        }
        if (remaining
            < MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Warning,
                "侧门结构局部搜索未通过，但总预算不足，已跳过全局恢复",
                details: new()
                {
                    ["remainingMs"] = remaining,
                    ["localRejection"] =
                        localResult.RejectionReason.ToString()
                });
            return false;
        }

        tuning.StructureFallbackBudgetMilliseconds = Math.Min(
            tuning.StructureFallbackBudgetMilliseconds,
            remaining);
        return true;
    }
}
/*
 * 文件职责：MapCvAlignmentService.Budget。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
