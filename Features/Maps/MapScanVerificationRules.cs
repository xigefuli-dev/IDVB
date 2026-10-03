namespace IDVBuff.Features.Maps;

internal static class MapScanVerificationRules
{
    internal static MapStructureRegistrationTuning CreateTuning(MapStructureRegistrationTuning source)
    {
        var tuning = source.Clone();
        tuning.Mode = MapStructureRegistrationMode.ScanVerification;
        tuning.EnableScanCheapReject = false;
        tuning.EnableScanCheapRejectShadowCollection = true;
        tuning.StructureFallbackBudgetMilliseconds = MapOpenAlignmentRouteRules.ScanVerificationFormalStructureBudgetMilliseconds;
        tuning.EnableFeatureVoting = false;
        tuning.EnableEccRefinement = false;
        tuning.EnableFastAlignment = true;
        tuning.FastFallbackToLegacy = false;
        tuning.FastAlignmentShadowMode = false;
        tuning.FastCoarseTopK = 2;
        tuning.MaximumTranslationCandidates = 2;
        tuning.TopCandidateCount = 2;
        tuning.PreviousAlignmentSearchRadiusPixels = 48;
        tuning.DisableScaleEarlyTermination = false;
        tuning.EnableVisibleMask = false;
        tuning.EnableVisibleAwareShadow = false;
        tuning.EnableVisibleAwareInjection = false;
        tuning.EnableVisibleAwareEarlyExit = false;
        tuning.Normalize();
        return tuning;
    }
}
