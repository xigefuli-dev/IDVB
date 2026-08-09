using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed record MapStructureCandidate
{
    public double Scale { get; init; }
    public int ReferenceX { get; init; }
    public int ReferenceY { get; init; }
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double ChamferPixels { get; init; }
    public double EdgeCoverage { get; init; }
    public double OccupancyCoverage { get; init; }
    public int ConsistentPartitions { get; init; }
    public bool UsedGlobalSearch { get; init; }
    public double CompositeCost { get; init; }
    public int FeatureInlierCount { get; init; }
    public double FeatureConsensus { get; init; }
    public double PriorAgreement { get; init; } = 1d;
    public bool IsWithinValidBounds { get; init; } = true;
    public bool EccConverged { get; init; }
    public double EccCorrelation { get; init; }
    public bool FromVisibleAware { get; init; }
    public double VisibleFraction { get; init; }
    public int VisibleStructurePixels { get; init; }
    public int VisibleEdgePixels { get; init; }
}

public sealed class MapStructureRegistrationResult
{
    public bool Accepted { get; init; }
    public MapOverlayTransform? Transform { get; init; }
    public double Confidence { get; init; }
    public MapStructureConfidenceBreakdown? ConfidenceBreakdown { get; init; }
    public double BestScore { get; init; }
    public double SecondScore { get; init; }
    public double CandidateMargin { get; init; }
    public MapStructureRejectionReason RejectionReason { get; init; }
    public string FailureReason { get; init; } = string.Empty;
    public IReadOnlyList<MapStructureCandidate> Candidates { get; init; } = [];
    public double PreprocessMilliseconds { get; init; }
    public double SearchMilliseconds { get; init; }
    public double RefineMilliseconds { get; init; }
    public double DistanceMapMilliseconds { get; init; }
    public double QueryConstructionMilliseconds { get; init; }
    public double HistoryCandidateMilliseconds { get; init; }
    public double FeatureVotingMilliseconds { get; init; }
    public double PyramidSearchMilliseconds { get; init; }
    public double LocalTemplateSearchMilliseconds { get; init; }
    public double GlobalTemplateSearchMilliseconds { get; init; }
    public double CandidateRankingMilliseconds { get; init; }
    public string? DebugOutputDirectory { get; init; }
    public double LockedScale { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public int QueryEdgePixels { get; init; }
    public int QueryBoundsX { get; init; }
    public int QueryBoundsY { get; init; }
    public int QueryBoundsWidth { get; init; }
    public int QueryBoundsHeight { get; init; }
    public int ScaleHypothesisCount { get; init; }
    public int OversizedHypothesisCount { get; init; }
    public bool UsedRestrictedSearch { get; init; }
    public bool WasForcedBestCandidate { get; init; }
    public int FeatureMatchCount { get; init; }
    public int FeatureInlierCount { get; init; }
    public double FeatureConsensus { get; init; }
    public bool EccConverged { get; init; }
    public double EccCorrelation { get; init; }
    public double VisibleMaskMilliseconds { get; init; }
    public double VisibleFraction { get; init; }
    public int VisibleStructurePixels { get; init; }
    public int VisibleEdgePixels { get; init; }
    public double VisibleAwareSearchMilliseconds { get; init; }
    public int VisibleAwareCandidateCount { get; init; }
    public double VisibleAwareTopCost { get; init; }
    public double VisibleAwareTopMargin { get; init; }
    public bool VisibleAwareEarlyAccepted { get; init; }
    public string? VisibleAwareFallbackReason { get; init; }

    // Fast alignment diagnostics
    public bool UsedFastStrategy { get; init; }
    public double FastCoarseSearchMilliseconds { get; init; }
    public int FastCoarseCandidateCount { get; init; }

    public static MapStructureRegistrationResult Reject(
        MapStructureRejectionReason reason,
        string? detail = null,
        IReadOnlyList<MapStructureCandidate>? candidates = null,
        double preprocessMilliseconds = 0d,
        double searchMilliseconds = 0d,
        string? debugOutputDirectory = null,
        double lockedScale = 0d,
        int referenceWidth = 0,
        int referenceHeight = 0,
        int queryEdgePixels = 0,
        Rect? queryBounds = null,
        int scaleHypothesisCount = 0,
        int oversizedHypothesisCount = 0,
        bool usedRestrictedSearch = false,
        double visibleMaskMilliseconds = 0d,
        double visibleFraction = 0d,
        int visibleStructurePixels = 0,
        int visibleEdgePixels = 0,
        double visibleAwareSearchMilliseconds = 0d,
        int visibleAwareCandidateCount = 0,
        double visibleAwareTopCost = 0d,
        double visibleAwareTopMargin = 0d,
        bool visibleAwareEarlyAccepted = false,
        string? visibleAwareFallbackReason = null,
        double distanceMapMilliseconds = 0d,
        double queryConstructionMilliseconds = 0d,
        double historyCandidateMilliseconds = 0d,
        double featureVotingMilliseconds = 0d,
        double pyramidSearchMilliseconds = 0d,
        double localTemplateSearchMilliseconds = 0d,
        double globalTemplateSearchMilliseconds = 0d,
        double candidateRankingMilliseconds = 0d) =>
        new()
        {
            RejectionReason = reason,
            FailureReason = string.IsNullOrWhiteSpace(detail)
                ? reason.ToDisplayText()
                : detail,
            Candidates = candidates ?? [],
            PreprocessMilliseconds = preprocessMilliseconds,
            SearchMilliseconds = searchMilliseconds,
            DebugOutputDirectory = debugOutputDirectory,
            LockedScale = lockedScale,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            QueryEdgePixels = queryEdgePixels,
            QueryBoundsX = queryBounds?.X ?? 0,
            QueryBoundsY = queryBounds?.Y ?? 0,
            QueryBoundsWidth = queryBounds?.Width ?? 0,
            QueryBoundsHeight = queryBounds?.Height ?? 0,
            ScaleHypothesisCount = scaleHypothesisCount,
            OversizedHypothesisCount = oversizedHypothesisCount,
            UsedRestrictedSearch = usedRestrictedSearch,
            VisibleMaskMilliseconds = visibleMaskMilliseconds,
            VisibleFraction = visibleFraction,
            VisibleStructurePixels = visibleStructurePixels,
            VisibleEdgePixels = visibleEdgePixels,
            VisibleAwareSearchMilliseconds = visibleAwareSearchMilliseconds,
            VisibleAwareCandidateCount = visibleAwareCandidateCount,
            VisibleAwareTopCost = visibleAwareTopCost,
            VisibleAwareTopMargin = visibleAwareTopMargin,
            VisibleAwareEarlyAccepted = visibleAwareEarlyAccepted,
            VisibleAwareFallbackReason = visibleAwareFallbackReason,
            DistanceMapMilliseconds = distanceMapMilliseconds,
            QueryConstructionMilliseconds = queryConstructionMilliseconds,
            HistoryCandidateMilliseconds = historyCandidateMilliseconds,
            FeatureVotingMilliseconds = featureVotingMilliseconds,
            PyramidSearchMilliseconds = pyramidSearchMilliseconds,
            LocalTemplateSearchMilliseconds = localTemplateSearchMilliseconds,
            GlobalTemplateSearchMilliseconds = globalTemplateSearchMilliseconds,
            CandidateRankingMilliseconds = candidateRankingMilliseconds
        };
}
