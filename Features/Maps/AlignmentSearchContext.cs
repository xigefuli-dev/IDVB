namespace IDVBuff.Features.Maps;

public sealed class AlignmentSearchContext
{
    public required GateSearchContext GateSearch { get; init; }

    public bool UseRestrictedStructureFallback { get; init; }

    /// <summary>
    /// The restricted seed came from the current scan, but no alignment has
    /// been committed yet. If its local basin fails, recovery must use the
    /// full initial scale range instead of the narrow tracking range.
    /// </summary>
    public bool UseInitialHighPrecisionRecovery { get; init; }
    public bool RequireCurrentFrameEvidence { get; init; }
    public bool AllowFullSearchUpgrade { get; init; }

    public MapRecognitionAttempt? PreviousAttempt { get; init; }
    public MapSimilarityTransform? ExpectedTransform { get; init; }
}
