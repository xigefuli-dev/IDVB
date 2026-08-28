using OpenCvSharp;

namespace IDVBuff.Features.Maps;
internal static partial class MapCvRecognitionBuilders
{

    internal static RuntimeMapRecognition BuildFloorStructureRecognition(
        MapRecord map,
        string floorKey,
        string overlayPath,
        MapOverlayTransform transform,
        MapStructureRegistrationResult structure,
        double identityPriorConfidence = 0d) =>
        new()
        {
            Map = map,
            FloorImagePath = overlayPath,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floorKey,
                OrientationDegrees =
                    MapFloorRules.GetFloorProfile(map, floorKey)?.OrientationDegrees ?? 0,
                Confidence = structure.Confidence,
                IdentityConfidence = double.IsFinite(identityPriorConfidence)
                    && identityPriorConfidence > 0d
                        ? Math.Clamp(identityPriorConfidence, 0d, 1d)
                        : structure.Confidence,
                LocalizationConfidence = structure.Confidence,
                Source = MapRecognitionSource.StructureMatching,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = true,
                OverlayTransform = transform,
                AnchorMatches = [],
                StructureBestScore = structure.BestScore,
                StructureSecondScore = structure.SecondScore,
                StructureCandidateMargin = structure.CandidateMargin,
                StructureRejectionReason = structure.RejectionReason,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    structure.RejectionReason.ToDisposition(
                        structure.Accepted),
                WasForcedBestResult = false
            }
        };

    internal static RuntimeMapRecognition BuildReusedTransformRecognition(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapStructureRegistrationResult? structure) =>
        new()
        {
            Map = fingerprint.Map,
            FloorImagePath = fingerprint.OverlayImagePath,
            Result = new MapRecognitionResult
            {
                MapId = fingerprint.Map.Id,
                Floor = fingerprint.FloorKey,
                OrientationDegrees = 0,
                Confidence = session.LastConfidence,
                IdentityConfidence = session.LastConfidence,
                LocalizationConfidence = session.LastConfidence,
                Source = MapRecognitionSource.ReusedLastTransform,
                HasAllRequiredAnchorEvidence = false,
                UsedLocalConfirmation = false,
                OverlayTransform = session.LockedTransform,
                StructureBestScore =
                    structure?.BestScore ?? session.LastBestScore,
                StructureSecondScore =
                    structure?.SecondScore ?? session.LastSecondScore,
                StructureCandidateMargin =
                    structure?.CandidateMargin
                    ?? session.LastCandidateMargin,
                StructureRejectionReason =
                    structure?.RejectionReason
                    ?? MapStructureRejectionReason.NoCandidate,
                WasForcedBestResult = true,
                ReusedLastTransform = true,
                EvidenceKind = MapAlignmentEvidenceKind.None,
                StructureDisposition =
                    (structure?.RejectionReason
                        ?? MapStructureRejectionReason.NoCandidate)
                    .ToDisposition()
            }
        };

    internal static MapRecognitionAttempt ReuseLastTransformAttempt(
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession session,
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult? structure = null)
    {
        // 侧门扫描只确认地图身份，不提供双门缩放锁定；其会话的锁定变换可能
        // 来自单门或辅助锚点，位置不够可信。直接复用可能继续渲染错误的叠加层，
        // 因此侧门策略下不复用，返回失败让下一帧重新扫描。
        if (session.SideEntranceScanPriorConfidence > 0d)
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.NeedsGatePair;
            diagnostics.StructureRejectionReason =
                MapStructureRejectionReason.AnchorTransformConflict;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "侧门扫描会话尚未建立可信的双门缩放锁定，已拒绝复用上次变换，等待重新扫描。");
        }

        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.HoldingLastTransform;
        diagnostics.UsedForcedBestResult = true;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = BuildReusedTransformRecognition(
                fingerprint,
                session,
                structure)
        };
    }

    internal static RuntimeMapRecognition MarkForcedBestResult(
        RuntimeMapRecognition original)
    {
        var result = original.Result;
        return new RuntimeMapRecognition
        {
            Map = original.Map,
            FloorImagePath = original.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = result.OrientationDegrees,
                Confidence = result.Confidence,
                IdentityConfidence = result.IdentityConfidence,
                LocalizationConfidence = result.LocalizationConfidence,
                Source = result.Source,
                HasAllRequiredAnchorEvidence =
                    result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = result.OverlayTransform,
                AnchorMatches = result.AnchorMatches,
                StructureBestScore = result.StructureBestScore,
                StructureSecondScore = result.StructureSecondScore,
                StructureCandidateMargin =
                    result.StructureCandidateMargin,
                StructureRejectionReason =
                    result.StructureRejectionReason,
                WasForcedBestResult = true,
                ReusedLastTransform = result.ReusedLastTransform,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation =
                    result.SkippedStructureValidation
            }
        };
    }

    internal static IReadOnlyList<MapRecognitionChoice> BuildChoices(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        MapRecognitionSource source,
        int maxCount = 9)
    {
        var choices = new List<MapRecognitionChoice>();
        foreach (var candidate in ranked.Take(maxCount))
        {
            if (candidate.VectorError > tuning.VectorErrorTolerance)
                continue;
            if (!TryBuildRecognition(
                    candidate,
                    alignmentMode,
                    tuning,
                    margin,
                    usedConfirmation: false,
                    source,
                    wasForcedBestResult: false,
                    out var recognition,
                    out _))
            {
                continue;
            }

            choices.Add(new MapRecognitionChoice
            {
                Recognition = recognition!,
                VectorError = candidate.VectorError
            });
        }

        return choices;
    }

    /// <summary>
    /// Constructs a rejected-structure attempt uniform across the scale-change
    /// and side-entrance-deviation rejection branches, keeping the caller
    /// responsible for producing the <paramref name="rejected"/> result and the
    /// human-readable <paramref name="failureReason"/>.
    /// </summary>
    internal static MapRecognitionAttempt BuildStructureRejectedAttempt(
        MapScanDiagnostics diagnostics,
        MapStructureRegistrationResult rejected,
        string failureReason,
        GateDetectionResult? gateResult,
        AlignmentSearchStage searchStage)
    {
        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.HoldingLastTransform;
        diagnostics.StructureRejectionReason = rejected.RejectionReason;
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = false;
        diagnostics.StructureFailureReason = rejected.FailureReason;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = rejected,
            FailureReason = failureReason,
            GateDetectionResult = gateResult,
            SearchStage = searchStage,
            StructureAttempted = true,
            StructureAccepted = false,
            StructureFailureReason = rejected.FailureReason,
        };
    }

    internal static MapRecognitionAttempt FailureWithChoices(
        IReadOnlyList<MapGeometryCandidate> ranked,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        double margin,
        MapRecognitionSource source,
        MapScanDiagnostics diagnostics,
        string reason,
        int maxCandidates = 9)
    {
        var choices = BuildChoices(ranked, alignmentMode, tuning, margin, source, maxCandidates);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            Choices = choices,
            FailureReason = choices.Count > 0
                ? reason + " 请从候选中选择，或取消后重试。"
                : reason
        };
    }
}
