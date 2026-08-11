namespace IDVBuff.Features.Maps;

/// <summary>
/// Builds the persisted research record for one completed high-level
/// alignment route.  Keeping this mapping separate makes success and failure
/// serialization testable without invoking OpenCV or the UI orchestrator.
/// </summary>
public static class MapAlignmentResearchAttemptFactory
{
    public static MapAlignmentResearchAttempt Create(
        MapRecord map,
        string floorKey,
        MapRecognitionAttempt attempt,
        MapRuntimeSettings settings,
        MapSessionSnapshot session,
        MapWindowSignature windowSignature,
        string floorSource,
        MapOverlayTransform? scaleSeed = null,
        IReadOnlyList<double>? searchRadii = null,
        int stableConfirmationFrames = 0,
        int stableConfirmationRequiredFrames = 0,
        bool calibrationUpdated = false,
        string? calibrationRejectionReason = null,
        RuntimeMapRecognition? recognitionOverride = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(windowSignature);

        var structure = attempt.StructureResult;
        var recognition = recognitionOverride ?? attempt.Recognition;
        var transform = recognition?.Result.OverlayTransform
            ?? structure?.Transform;
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var learned = settings.FloorScaleCalibrations.FirstOrDefault(candidate =>
            candidate.Matches(map.Id, map.UpdatedAt, primaryFloorKey, floorKey));
        var confidence = recognition?.Result.Confidence
            ?? structure?.Confidence
            ?? 0d;
        var elapsed = attempt.Diagnostics.TotalMilliseconds;
        if (elapsed <= 0d)
        {
            elapsed = (structure?.PreprocessMilliseconds ?? 0d)
                + (structure?.SearchMilliseconds ?? 0d)
                + (structure?.RefineMilliseconds ?? 0d);
        }

        var failureReason = string.IsNullOrWhiteSpace(attempt.FailureReason)
            ? attempt.StructureFailureReason
            : attempt.FailureReason;

        return new MapAlignmentResearchAttempt
        {
            SessionVersion = session.Version,
            AlignmentRevision = session.AlignmentRevision,
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = floorKey,
            FloorPosition = MapFloorRules.GetFloorPosition(map, floorKey),
            FloorSource = floorSource,
            WindowSignature = windowSignature,
            ReferenceWidth = transform?.ReferenceWidth
                ?? structure?.ReferenceWidth
                ?? 0,
            ReferenceHeight = transform?.ReferenceHeight
                ?? structure?.ReferenceHeight
                ?? 0,
            ValidMapBounds = MapFloorRules.GetFloorProfile(map, floorKey)
                ?.GetEffectiveValidMapBounds(),
            PrimaryScale = scaleSeed?.ScaleX
                ?? (string.Equals(floorKey, primaryFloorKey, StringComparison.Ordinal)
                    ? transform?.ScaleX
                    : null),
            HistoricalFloorRatio = learned?.MedianRatio,
            ScaleSeedSource = scaleSeed is null ? "double-gate" : "cross-floor",
            SearchStages = (searchRadii ?? [])
                .Select(radius => new MapAlignmentResearchSearchStage(
                    radius,
                    structure?.ScaleHypothesisCount ?? 0,
                    UsedGlobalTranslationSearch: true))
                .ToArray(),
            QueryEdgePixels = structure?.QueryEdgePixels ?? 0,
            QueryBoundsWidth = structure?.QueryBoundsWidth ?? 0,
            QueryBoundsHeight = structure?.QueryBoundsHeight ?? 0,
            FeatureMatchCount = structure?.FeatureMatchCount ?? 0,
            FeatureInlierCount = structure?.FeatureInlierCount ?? 0,
            GateCandidateCount = attempt.Diagnostics.GateCandidateCount,
            AnchorMatches = recognition?.Result.AnchorMatches ?? [],
            EvidenceKind = recognition?.Result.EvidenceKind
                ?? MapAlignmentEvidenceKind.None,
            Candidates = structure?.Candidates.Take(20).ToArray() ?? [],
            ConfidenceBreakdown = structure?.ConfidenceBreakdown,
            FinalTransform = transform,
            Confidence = confidence,
            IsHighConfidence = confidence >= settings.SessionTuning.HighConfidence,
            Accepted = recognition is not null,
            StableConfirmationFrames = stableConfirmationFrames,
            StableConfirmationRequiredFrames = stableConfirmationRequiredFrames,
            CalibrationUpdated = calibrationUpdated,
            CalibrationRejectionReason = calibrationRejectionReason,
            ElapsedMilliseconds = elapsed,
            FailureCategory = recognition is null
                ? MapAlignmentResearchFailureClassifier.Classify(attempt)
                : MapAlignmentResearchFailureCategory.None,
            FailureReason = recognition is null ? failureReason : string.Empty
        };
    }
}
