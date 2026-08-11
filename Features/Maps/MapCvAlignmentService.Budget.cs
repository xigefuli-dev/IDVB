namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt? ApplyNoDoorBudgetBeforeLocalSearch(
        MapStructureRegistrationTuning tuning,
        bool isSideEntranceStructureRoute,
        MapScanDiagnostics diagnostics,
        GateDetectionResult? gateResult)
    {
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
                is not { } remaining)
        {
            return null;
        }
        if (remaining
            < MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds)
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
