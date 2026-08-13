namespace IDVBuff.Features.Maps;

public enum MapAlignmentResearchFailureCategory
{
    None,
    NoVisualFeatures,
    InsufficientFeatures,
    InsufficientStructure,
    NoCandidate,
    WeakFit,
    AmbiguousCandidates,
    ScaleOutOfRange,
    BoundsOrPlayerPriorConflict,
    Timeout,
    SystemError
}

public sealed record MapAlignmentResearchSearchStage(
    double ScaleRadius,
    int ScaleHypothesisCount,
    bool UsedGlobalTranslationSearch);

public sealed record MapAlignmentResearchAttempt
{
    public int SchemaVersion { get; init; } = 1;
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>Map-open session 版本号，用于关联同一对局的多次对齐。</summary>
    public long SessionVersion { get; init; }
    /// <summary>对齐修订号，在同一 session 内递增。</summary>
    public long AlignmentRevision { get; init; }
    public Guid MapId { get; init; }
    public DateTimeOffset MapUpdatedAt { get; init; }
    public string FloorKey { get; init; } = string.Empty;
    public int FloorPosition { get; init; }
    public string FloorSource { get; init; } = string.Empty;
    public MapWindowSignature? WindowSignature { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public MapReferenceBounds? ValidMapBounds { get; init; }
    public double? PrimaryScale { get; init; }
    public double? HistoricalFloorRatio { get; init; }
    public string ScaleSeedSource { get; init; } = string.Empty;
    public string ScaleSeedCacheSource { get; init; } = string.Empty;
    public bool ScaleSeedProjected { get; init; }
    public int ScaleSeedSourceViewportWidth { get; init; }
    public int ScaleSeedSourceViewportHeight { get; init; }
    public int ScaleSeedTargetViewportWidth { get; init; }
    public int ScaleSeedTargetViewportHeight { get; init; }
    public double? ProjectedScale { get; init; }
    public double? FinalValidatedScale { get; init; }
    public string ScaleSeedRejectionReason { get; init; } = string.Empty;
    public IReadOnlyList<MapAlignmentResearchSearchStage> SearchStages { get; init; } = [];
    public int QueryEdgePixels { get; init; }
    public int QueryBoundsWidth { get; init; }
    public int QueryBoundsHeight { get; init; }
    public int FeatureMatchCount { get; init; }
    public int FeatureInlierCount { get; init; }
    public int GateCandidateCount { get; init; }
    public IReadOnlyList<CvAnchorEvidence> AnchorMatches { get; init; } = [];
    public MapAlignmentEvidenceKind EvidenceKind { get; init; }
    public IReadOnlyList<MapStructureCandidate> Candidates { get; init; } = [];
    public MapStructureConfidenceBreakdown? ConfidenceBreakdown { get; init; }
    public MapOverlayTransform? FinalTransform { get; init; }
    public double Confidence { get; init; }
    public bool IsHighConfidence { get; init; }
    public bool Accepted { get; init; }
    public int StableConfirmationFrames { get; init; }
    public int StableConfirmationRequiredFrames { get; init; }
    public bool CalibrationUpdated { get; init; }
    public string? CalibrationRejectionReason { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public MapAlignmentResearchFailureCategory FailureCategory { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

public static class MapAlignmentResearchFailureClassifier
{
    public static MapAlignmentResearchFailureCategory Classify(
        MapRecognitionAttempt attempt)
    {
        if (attempt.Recognition is not null)
            return MapAlignmentResearchFailureCategory.None;
        if (attempt.StructureResult is { } structure)
        {
            if (structure.QueryEdgePixels == 0
                && structure.RejectionReason
                    == MapStructureRejectionReason.InsufficientStructure)
            {
                return MapAlignmentResearchFailureCategory.NoVisualFeatures;
            }
            if (structure.FeatureMatchCount > 0
                && structure.FeatureInlierCount < 3
                && structure.RejectionReason is not (
                    MapStructureRejectionReason.TimeBudgetExceeded
                    or MapStructureRejectionReason.InvalidInput))
            {
                return MapAlignmentResearchFailureCategory.InsufficientFeatures;
            }
        }
        if (attempt.Diagnostics.GateCandidateCount == 0
            && attempt.StructureResult is null)
        {
            return MapAlignmentResearchFailureCategory.NoVisualFeatures;
        }

        return attempt.StructureResult?.RejectionReason switch
        {
            MapStructureRejectionReason.InsufficientStructure =>
                MapAlignmentResearchFailureCategory.InsufficientStructure,
            MapStructureRejectionReason.NoCandidate =>
                MapAlignmentResearchFailureCategory.NoCandidate,
            MapStructureRejectionReason.WeakAbsoluteScore
                or MapStructureRejectionReason.InconsistentStructure
                or MapStructureRejectionReason.RefinementFailed =>
                MapAlignmentResearchFailureCategory.WeakFit,
            MapStructureRejectionReason.AmbiguousCandidates =>
                MapAlignmentResearchFailureCategory.AmbiguousCandidates,
            MapStructureRejectionReason.InvalidLockedScale
                or MapStructureRejectionReason.ScaleChangeTooLarge
                or MapStructureRejectionReason.NativeScaleChanged =>
                MapAlignmentResearchFailureCategory.ScaleOutOfRange,
            MapStructureRejectionReason.OutsideValidBounds
                or MapStructureRejectionReason.PlayerPriorMismatch
                or MapStructureRejectionReason.AnchorTransformConflict =>
                MapAlignmentResearchFailureCategory.BoundsOrPlayerPriorConflict,
            MapStructureRejectionReason.TimeBudgetExceeded =>
                MapAlignmentResearchFailureCategory.Timeout,
            MapStructureRejectionReason.InvalidInput
                when attempt.Diagnostics.GateCandidateCount > 0 =>
                MapAlignmentResearchFailureCategory.InsufficientFeatures,
            _ => MapAlignmentResearchFailureCategory.SystemError
        };
    }
}
