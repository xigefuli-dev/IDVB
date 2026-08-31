// IDVB Real CLI — 从 SessionOrchestrator 只读状态构建输出 DTO
//
// run / mapopen 命令共用同一套 DTO 映射（只读属性访问，不走任何识别逻辑），
// 避免两个命令各自复制映射代码导致漂移。

using IDVBuff.Features.Maps;
using IDVBuff.RealCLI.Output;

namespace IDVBuff.RealCLI.Cli;

internal static class SessionResultBuilder
{
    public static RealCliRecognitionOutput? BuildRecognition(SessionOrchestrator orchestrator)
    {
        var rec = orchestrator.LastRecognition;
        if (rec is null)
            return null;
        return new RealCliRecognitionOutput
        {
            MapId = rec.Map.Id.ToString(),
            MapDisplayName = rec.Map.DisplayName,
            Floor = rec.Result.Floor,
            Confidence = rec.Result.Confidence,
            RecognitionSource = rec.Result.Source.ToString(),
            HasAllRequiredAnchorEvidence = rec.Result.HasAllRequiredAnchorEvidence,
            GeometryMargin = rec.Result.GeometryMargin,
            FloorImagePath = rec.FloorImagePath,
            Transform = rec.Result.OverlayTransform is { } t
                ? new RealCliTransformOutput
                {
                    ScaleX = t.ScaleX,
                    ScaleY = t.ScaleY,
                    OffsetX = t.OffsetX,
                    OffsetY = t.OffsetY,
                    ReferenceWidth = t.ReferenceWidth,
                    ReferenceHeight = t.ReferenceHeight
                }
                : null
        };
    }

    public static RealCliDiagnosticsOutput? BuildDiagnostics(SessionOrchestrator orchestrator)
    {
        var diag = orchestrator.LastDiagnostics;
        if (diag is null)
            return null;
        return new RealCliDiagnosticsOutput
        {
            PreprocessMs = diag.PreprocessMilliseconds,
            GateDetectionMs = diag.GateDetectionMilliseconds,
            GeometryMs = diag.GeometryMilliseconds,
            CacheMs = diag.CacheMilliseconds,
            StructureSearchMs = diag.StructureSearchMilliseconds,
            StructureRefineMs = diag.StructureRefineMilliseconds,
            OverlayMs = diag.OverlayMilliseconds,
            TotalMs = diag.TotalMilliseconds,
            GateCandidateCount = diag.GateCandidateCount,
            EvidenceKind = diag.AlignmentEvidence.ToString(),
            StructureAttempted = diag.StructureAttempted,
            StructureAccepted = diag.StructureAccepted,
            SearchStage = diag.SearchStage.ToString(),
            LowStructureRoute = diag.LowStructureRoute,
            LowStructureReadinessDecision = diag.LowStructureReadinessDecision,
            LowStructureCacheTrustLevel = diag.LowStructureCacheTrustLevel,
            LowStructurePlannedScaleCount = diag.LowStructurePlannedScaleCount,
            LowStructureCompletedScaleCount = diag.LowStructureCompletedScaleCount,
            LowStructureRecoveryBatch = diag.LowStructureRecoveryBatch,
            LowStructureRecoveryTotalScaleCount =
                diag.LowStructureRecoveryTotalScaleCount,
            LowStructureTranslationCandidateCount =
                diag.LowStructureTranslationCandidateCount,
            LowStructureBudgetTerminationReason =
                diag.LowStructureBudgetTerminationReason,
            LowStructureVpsgEnabled = diag.LowStructureVpsgEnabled,
            VpsgActuallyEnabled = diag.VpsgActuallyEnabled,
            StructureBestScore = diag.StructureBestScore,
            StructureCandidateMargin = diag.StructureCandidateMargin
        };
    }

    public static RealCliAlignmentSessionOutput? BuildAlignmentSession(
        SessionOrchestrator orchestrator)
    {
        var alignSession = orchestrator.LastAlignmentSession;
        if (alignSession is null)
            return null;
        var predictedRoute = alignSession.SideEntranceScanPriorConfidence > 0d
            ? "SideEntrance"
            : alignSession.HasGatePairLock
                ? "DualGate (Standard)"
                : "StructureOnly / Fallback";
        return new RealCliAlignmentSessionOutput
        {
            MapId = alignSession.MapId.ToString(),
            FloorKey = alignSession.FloorKey,
            Mode = alignSession.Mode.ToString(),
            SideEntranceScanPriorConfidence = alignSession.SideEntranceScanPriorConfidence,
            HasGatePairLock = alignSession.HasGatePairLock,
            BaselineGateScale = alignSession.BaselineGateScale,
            LastConfidence = alignSession.LastConfidence,
            LastBestScore = alignSession.LastBestScore,
            ConsecutiveRejections = alignSession.ConsecutiveRejections,
            LastStructureAccepted = alignSession.LastStructureAccepted,
            LastStructureFailureReason = alignSession.LastStructureFailureReason,
            ConsecutiveStructureFailures = alignSession.ConsecutiveStructureFailures,
            PredictedAlignmentRoute = predictedRoute,
        };
    }

    public static List<RealCliLogEntrySummary> BuildLogEntries(SessionOrchestrator orchestrator)
    {
        var entries = orchestrator.LogCollector.GetEntries();
        if (entries is not { Count: > 0 })
            return [];
        var logEntries = new List<RealCliLogEntrySummary>();
        foreach (var e in entries.TakeLast(50))
        {
            logEntries.Add(new RealCliLogEntrySummary
            {
                Category = e.Category.ToString(),
                Level = e.Level.ToString(),
                Message = $"[{e.Timestamp:HH:mm:ss}] {e.Message}",
                ElapsedMs = e.ElapsedMs ?? 0
            });
        }
        return logEntries;
    }

    public static List<RealCliCandidateChoiceOutput>? BuildCandidateChoices(
        SessionOrchestrator orchestrator)
    {
        var choices = orchestrator.LastCandidateChoices;
        if (choices is not { Count: > 0 })
            return null;
        return choices
            .Select(choice => new RealCliCandidateChoiceOutput
            {
                MapId = choice.Recognition.Map.Id.ToString(),
                MapDisplayName = choice.Recognition.Map.DisplayName,
                Floor = choice.Recognition.Result.Floor,
                RawConfidence = choice.RawConfidence,
                IsReferenceOnly = choice.IsReferenceOnly,
                EvidenceLabel = choice.EvidenceLabel,
                PreferredOrder = choice.PreferredOrder,
                TraditionalScore = choice.TraditionalScore,
                ModelProbability = choice.ModelProbability,
                FusionScore = choice.FusionScore,
                ModelMatchedFloorKey = choice.ModelMatchedFloorKey,
                ModelMatchedCenterX = choice.ModelMatchedCenterX,
                ModelMatchedCenterY = choice.ModelMatchedCenterY,
                ModelMatchedExtent = choice.ModelMatchedExtent,
                EvidenceSources = choice.EvidenceSources.ToString(),
                ModelVersion = choice.ModelVersion,
                ModelFailureReason = choice.ModelFailureReason,
                ModelInferenceMilliseconds = choice.ModelInferenceMilliseconds
            })
            .ToList();
    }

    public static RealCliModelStatusOutput BuildModelStatus(
        SessionOrchestrator orchestrator)
    {
        var status = orchestrator.MapLearningStatus;
        return new RealCliModelStatusOutput
        {
            IsAvailable = status.IsAvailable,
            IsQualified = status.IsQualified,
            CurrentVersion = status.CurrentVersion,
            LastKnownGoodVersion = status.LastKnownGoodVersion,
            LastFailureReason = status.LastFailureReason,
            PromotionBlockReason = status.PromotionBlockReason,
            HumanSelectionCount = status.HumanSelectionCount,
            LegacyHumanSelectionCount = status.LegacyHumanSelectionCount,
            MigratedLegacyHumanSelectionCount =
                status.MigratedLegacyHumanSelectionCount,
            DistinctMapCount = status.DistinctMapCount,
            ValidationMatchCount = status.ValidationMatchCount,
            ValidationAccuracy = status.ValidationAccuracy,
            TraditionalValidationAccuracy =
                status.TraditionalValidationAccuracy,
            TrustedSpatialValidationCount =
                status.TrustedSpatialValidationCount,
            SpatialValidationAccuracy = status.SpatialValidationAccuracy,
            SpatialMeanError = status.SpatialMeanError,
            LastRollbackReason = status.LastRollbackReason
        };
    }
}
