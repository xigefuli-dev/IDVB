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
        if (structureTuning.Mode == MapStructureRegistrationMode.ScanVerification
            && attempt.StructureAttempted)
            attempt.Diagnostics.ScanFormalStructureAttemptCount = 1;
        return attempt;
    }
}
