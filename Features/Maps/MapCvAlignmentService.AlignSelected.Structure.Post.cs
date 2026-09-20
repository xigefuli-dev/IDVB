using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt CompleteStructureAlignment(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        bool isSideEntranceStructureRoute,
        string? singleGateFallbackReason,
        RuntimeMapRecognition? singleGateProposal,
        GateDetectionResult gateResult,
        MapScanDiagnostics diagnostics,
        MapOverlayTransform? freshAnchorTransform,
        MapStructureRegistrationResult structure)
    {
        diagnostics.StructureSearchMilliseconds =
            structure.SearchMilliseconds;
        diagnostics.StructureRefineMilliseconds =
            structure.RefineMilliseconds;
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence =
            MapAlignmentEvidenceKind.Structure;
        PopulateStructureDiagnostics(diagnostics, structure);

        var effectiveStructureConfidence = structure.Confidence;

        var postStructureTimer = Stopwatch.StartNew();
        if (!structure.Accepted
            || structure.Transform is null
            || (effectiveStructureConfidence < tuning.MinimumConfidence
                && !tuning.ForceBestRecognitionResult))
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            if (tuning.ForceBestRecognitionResult)
            {
                diagnostics.UsedForcedBestResult = true;
                diagnostics.StructureAttempted = true;
                diagnostics.StructureAccepted = false;
                diagnostics.StructureFailureReason =
                    structure.FailureReason;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    StructureResult = structure,
                    Recognition = MapCvRecognitionBuilders
                        .BuildReusedTransformRecognition(
                            fingerprint,
                            session,
                            structure),
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                    StructureAttempted = true,
                    StructureAccepted = false,
                    StructureFailureReason = structure.FailureReason,
                };
            }

            var failureReason = structure.Accepted
                && structure.Confidence < tuning.MinimumConfidence
                    ? $"结构配准置信度 {structure.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}"
                    : structure.FailureReason;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = failureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason =
                    (singleGateFallbackReason is null
                        ? string.Empty
                        : $"{singleGateFallbackReason}；已回退结构配准，但")
                    + $"{failureReason}；已保留最后可靠对齐，等待下次开图恢复。",
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = failureReason,
            };
        }

        var gateBaseline = session.BaselineGateScale > 0d
            ? session.BaselineGateScale
            : session.LockedTransform.ScaleX;
        if (Math.Abs((structure.Transform.ScaleX / gateBaseline) - 1d)
                > structureTuning.ScaleSearchRadius + 0.0001d
            && !tuning.ForceBestRecognitionResult)
        {
            var rejected = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.ScaleChangeTooLarge,
                candidates: structure.Candidates,
                preprocessMilliseconds: structure.PreprocessMilliseconds,
                searchMilliseconds: structure.SearchMilliseconds,
                debugOutputDirectory: structure.DebugOutputDirectory);
            return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                diagnostics,
                rejected,
                $"{rejected.FailureReason}；已保留最后可靠对齐，等待双门重新锁定。",
                gateResult,
                diagnostics.SearchStage);
        }

        if (isSideEntranceStructureRoute && freshAnchorTransform is not null)
        {
            var maxDeviation = Math.Max(
                Math.Abs(
                    structure.Transform.OffsetX
                    - freshAnchorTransform.OffsetX),
                Math.Abs(
                    structure.Transform.OffsetY
                    - freshAnchorTransform.OffsetY));
            if (maxDeviation > MapCvRecognitionService.SideEntranceAnchorDeviationTolerancePixels)
            {
                var rejected = MapStructureRegistrationResult.Reject(
                    MapStructureRejectionReason.AnchorTransformConflict,
                    candidates: structure.Candidates,
                    preprocessMilliseconds: structure.PreprocessMilliseconds,
                    searchMilliseconds: structure.SearchMilliseconds,
                    debugOutputDirectory: structure.DebugOutputDirectory);
                return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                    diagnostics,
                    rejected,
                    $"{rejected.FailureReason}；结构结果与本次锚点位置偏差超过 "
                    + $"{MapCvRecognitionService.SideEntranceAnchorDeviationTolerancePixels:F0}px，已拒绝。",
                    gateResult,
                    diagnostics.SearchStage);
            }
        }

        diagnostics.TrackingMode = isSideEntranceStructureRoute
            ? MapAlignmentTrackingMode.StructureMatched
            : singleGateProposal is null
                ? MapAlignmentTrackingMode.StructureMatched
                : MapAlignmentTrackingMode.SingleGateTracking;
        diagnostics.UsedForcedBestResult =
            tuning.ForceBestRecognitionResult
            && (structure.WasForcedBestCandidate
                || structure.Confidence < tuning.MinimumConfidence);
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = structure.Accepted;
        diagnostics.StructureFailureReason =
            structure.Accepted ? string.Empty : structure.FailureReason;

        postStructureTimer.Stop();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构后处理完成 · {postStructureTimer.Elapsed.TotalMilliseconds:F0}ms",
            elapsedMs: postStructureTimer.Elapsed.TotalMilliseconds);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = MapCvRecognitionBuilders.BuildStructureRecognition(
                fingerprint,
                structure.Transform,
                structure,
                diagnostics.UsedForcedBestResult,
                singleGateProposal,
                confidenceOverride: null),
            GateDetectionResult = gateResult,
            SearchStage = diagnostics.SearchStage,
            StructureAttempted = true,
            StructureAccepted = structure.Accepted,
            StructureFailureReason =
                structure.Accepted ? string.Empty : structure.FailureReason,
        };
    }
}
