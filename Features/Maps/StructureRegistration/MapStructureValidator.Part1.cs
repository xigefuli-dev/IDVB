using OpenCvSharp;

namespace IDVBuff.Features.Maps;
internal static partial class MapStructureValidator
{
    internal static bool MeetsClearCorridorEvidence(
        MapStructureCandidate candidate,
        MapStructureRegistrationTuning tuning) =>
        tuning.Channel == MapAlignmentChannel.LowStructure
        && candidate.ReferenceCoverage >= 0.47d
        && candidate.ChamferPixels <= 1.20d
        && candidate.EdgeCoverage >= 0.90d
        && candidate.OccupancyCoverage >= 0.78d
        && candidate.ProjectionCorrelation >= 0.60d
        && candidate.ConsistentPartitions >= 3;

    /// <summary>
    /// A cold standard search with a neutral scale has no evidence outside its
    /// sampled interval. An accepted endpoint is therefore a censored estimate,
    /// not an independently located optimum, and must not seed the session.
    /// </summary>
    private static bool IsUncalibratedScaleSearchBoundary(
        MapStructureCandidate candidate,
        MapStructureRegistrationTuning tuning,
        MapStructureRegistrationRequest? request)
    {
        if (request is null
            || request.Channel != MapAlignmentChannel.Standard
            || request.ScaleSearchPolicy != MapScaleSearchPolicy.Search
            || request.TrackingMode
            || request.RestrictSearchToLockedTransform
            || request.ForceBestCandidate
            || request.SideEntrancePrior > 0d
            || request.CandidateHistory.Count > 0
            || !double.IsFinite(request.LockedTransform.ScaleX)
            || Math.Abs(request.LockedTransform.ScaleX - 1d) > 0.0005d)
        {
            return false;
        }

        var radius = Math.Max(
            tuning.ScaleSearchRadius,
            StructureRegistrationRules.ScaleSearchRadius);
        if (!double.IsFinite(radius) || radius <= 0d)
            return false;

        var lowerBoundary = request.LockedTransform.ScaleX * (1d - radius);
        var upperBoundary = request.LockedTransform.ScaleX * (1d + radius);
        var tolerance = Math.Max(
            0.0005d,
            StructureRegistrationRules.ScaleDuplicateTolerance * 0.5d);
        return Math.Abs(candidate.Scale - lowerBoundary) <= tolerance
            || Math.Abs(candidate.Scale - upperBoundary) <= tolerance;
    }

    internal static MapStructureRegistrationResult BuildLegacyResult(
        MapStructureRejectionReason rejectionReason,
        LegacyDiagnostics d,
        MapStructureCandidate[]? candidates = null,
        bool accepted = false,
        MapOverlayTransform? transform = null,
        double confidence = 0d,
        MapStructureConfidenceBreakdown? confidenceBreakdown = null,
        double bestScore = 0d,
        double secondScore = 0d,
        double candidateMargin = 0d,
        bool wasForcedBestCandidate = false,
        double featureConsensus = 0d,
        bool eccConverged = false,
        double eccCorrelation = 0d)
    {
        return BuildResult(rejectionReason,
            candidates: candidates,
            preprocessMs: d.PreprocessMs,
            searchMs: d.SearchMs,
            refineMs: d.RefineMs,
            queryConstructionMs: d.Ctx.QueryConstructionMs,
            historyCandidateMs: d.Ctx.HistoryCandidateMs,
            featureVotingMs: d.Ctx.FeatureVotingMs,
            pyramidSearchMs: d.Ctx.PyramidSearchMs,
            localTemplateSearchMs: d.Ctx.LocalTemplateSearchMs,
            globalTemplateSearchMs: d.Ctx.GlobalTemplateSearchMs,
            candidateRankingMs: d.CandidateRankingMs,
            debugDirectory: d.DebugDirectory,
            lockedScale: d.LockedScale,
            referenceWidth: d.ReferenceWidth,
            referenceHeight: d.ReferenceHeight,
            queryEdgePixels: d.QueryEdgePixels,
            queryBounds: d.QueryBounds,
            scaleHypothesisCount: d.ScaleHypothesisCount,
            oversizedHypothesisCount: d.OversizedHypothesisCount,
            usedRestrictedSearch: d.UsedRestrictedSearch,
            accepted: accepted,
            transform: transform,
            confidence: confidence,
            confidenceBreakdown: confidenceBreakdown,
            bestScore: bestScore,
            secondScore: secondScore,
            candidateMargin: candidateMargin,
            wasForcedBestCandidate: wasForcedBestCandidate,
            featureMatchCount: d.Ctx.FeatureMatchCount,
            featureInlierCount: d.Ctx.FeatureInlierCount,
            featureConsensus: featureConsensus,
            eccConverged: eccConverged,
            eccCorrelation: eccCorrelation,
            visibleMaskMs: d.VisibleMaskMs,
            visibleFraction: d.Ctx.VisibleAwareVisibleFraction ?? 0d,
            visibleStructurePixels: d.Ctx.VisibleAwareStructurePixels ?? 0,
            visibleEdgePixels: d.Ctx.VisibleAwareEdgePixels ?? 0,
            visibleAwareSearchMs: d.Ctx.VisibleAwareTotalMs,
            visibleAwareCandidateCount: d.Ctx.VisibleAwareCandidateCount,
            visibleAwareTopCost: d.Ctx.VisibleAwareBestCost,
            visibleAwareTopMargin: d.Ctx.VisibleAwareTopMargin,
            visibleAwareEarlyAccepted: d.Ctx.VisibleAwareEarlyAccepted,
            visibleAwareFallbackReason: d.Ctx.VisibleAwareFallbackReason,
            visibleAwareRequestedBackend: d.Ctx.VisibleAwareSession?.RequestedBackend ?? "",
            visibleAwareActualBackend: d.Ctx.VisibleAwareSession?.ActualBackend ?? "",
            visibleAwareUMatFallbackReason: d.Ctx.VisibleAwareSession?.FallbackReason,
            visibleAwareCoarseMs: d.Ctx.VisibleAwareCoarseMs,
            visibleAwareRefineMs: d.Ctx.VisibleAwareRefineMs,
            visibleAwareUploadMs: d.Ctx.VisibleAwareSession?.UploadMilliseconds ?? 0d,
            visibleAwareDownloadMs: d.Ctx.VisibleAwareSession?.DownloadMilliseconds ?? 0d,
            visibleAwareCompletedScales: d.Ctx.VisibleAwareCompletedScales,
            visibleAwareBudgetSkippedScales: d.Ctx.VisibleAwareBudgetSkippedScales,
            visibleAwareCoarsePeaks: d.Ctx.VisibleAwareCoarsePeaks,
            visibleAwareRefinedCandidates: d.Ctx.VisibleAwareRefinedCandidates,
            lowStructureRoute: d.LowStructurePlan?.Route.ToString(),
            lowStructureCompletedScaleCount: d.Ctx.ScalesEvaluated,
            lowStructureTranslationCandidateCount: d.Ctx.Candidates.Count,
            lowStructureBudgetTerminationReason: d.Ctx.TimeBudgetExceeded
                ? d.Ctx.WorkPreflightRejected
                    ? "work-preflight-budget-exceeded"
                    : "search-budget-exceeded"
                : string.Empty,
            lowStructureVpsgEnabled: false);
    }
}
