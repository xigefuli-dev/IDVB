namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static MapRecognitionAttempt AlignPrebuiltStructureLine(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapGeometryFingerprint fingerprint,
        MapAlignmentSession? session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var seed = session?.LockedTransform
            ?? MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                fingerprint.Map, fingerprint.FloorKey);
        var attempt = AlignStructureOnly(
            service, frame, selectedMapId, fingerprint.FloorKey, seed,
            alignmentMode, tuning, structureTuning, playerPrior,
            predictedViewportOrigin, liveIgnoreRegions, candidateHistory,
            isTracking: false,
            useProjectedBoundaryMask: false,
            allowPrimaryFloor: true,
            scaleSearchPolicy: session is null
                ? MapScaleSearchPolicy.Search
                : MapScaleSearchPolicy.Fixed,
            identityPriorConfidence: session?.LastConfidence ?? 0d,
            restrictTranslationToSeed: session is not null);
        // A selected identity may be re-opened at a different viewport origin. A stale
        // translation is a proposal, not a permanent restriction on this floor's search.
        // Automatic identity verification never enters this selected-map recovery branch.
        var remaining = structureTuning.StructureFallbackBudgetMilliseconds - (int)timer.ElapsedMilliseconds;
        if (!attempt.StructureAccepted && session is not null
            && structureTuning.Mode != MapStructureRegistrationMode.ScanVerification && remaining > 25
            && MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is not <= 25)
        {
            var recovery = structureTuning.Clone();
            recovery.StructureFallbackBudgetMilliseconds = remaining;
            attempt = AlignStructureOnly(service, frame, selectedMapId, fingerprint.FloorKey, seed,
                alignmentMode, tuning, recovery, playerPrior, predictedViewportOrigin,
                liveIgnoreRegions, candidateHistory, isTracking: false,
                useProjectedBoundaryMask: false, allowPrimaryFloor: true,
                scaleSearchPolicy: MapScaleSearchPolicy.Fixed,
                identityPriorConfidence: session.LastConfidence, restrictTranslationToSeed: false);
        }
        if (structureTuning.Mode == MapStructureRegistrationMode.ScanVerification
            && attempt.StructureAttempted)
            attempt.Diagnostics.ScanFormalStructureAttemptCount = 1;
        return attempt;
    }
}
