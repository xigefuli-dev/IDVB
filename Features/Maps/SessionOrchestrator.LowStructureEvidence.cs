namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static LowStructureScaleEvidenceRules.AlignmentEvidence ObserveLowStructureEvidence(
        MapRecognitionAttempt attempt) => LowStructureScaleEvidenceRules.ObserveAlignment(attempt);

    private async Task PersistLowStructureScaleAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame,
        MapScanDiagnostics diagnostics)
    {
        if (_settings?.AllowAutomaticMapCache is not true
            || MapAlignmentChannelRegistry.Resolve(
                recognition.Map,
                recognition.Result.Floor).Channel
                != MapAlignmentChannel.LowStructure
            || diagnostics.LowStructureEvidenceCount
                < LowStructureScaleEvidenceRules.MinimumIndependentScaleConfirmations
            || recognition.Result.OverlayTransform is not { } transform
            || !TryGetUniformScale(transform, out var scale))
        {
            return;
        }

        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return;
        var tuning = CreateStructureTuningForFloor(
            recognition.Map,
            recognition.Result.Floor,
            CreateEffectiveStructureTuning());
        var key = CreateAlignmentCacheKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution,
            tuning);
        var confidence = recognition.Result.LocalizationConfidence;
        var margin = MapFeatureCacheRules.GetCandidateMargin(recognition.Result);
        if (diagnostics.LowStructureEvidenceCount
            >= LowStructureScaleEvidenceRules.MinimumIndependentScaleConfirmations)
        {
            if (_mapFeatureCacheRepository.TryGet(key, out var protectedEntry)
                && protectedEntry?.Scale.Source is MapFeatureCacheSource.Manual
                    or MapFeatureCacheSource.Player)
            {
                return;
            }
            var confirmedEntry = CreateCacheEntry(
                key,
                scale,
                MapFeatureCacheSource.Recovery,
                diagnostics.LowStructureEvidenceCount,
                confidence,
                relativeMad: diagnostics.LowStructureScaleRelativeMad,
                DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
                validation: new MapScaleCacheValidationMetadata
                {
                    DirectlyTrusted = false,
                    LowStructureTrustLevel = LowStructureCacheTrustLevel.Trusted,
                    SuccessfulValidationCount = diagnostics.LowStructureEvidenceCount,
                    FailedValidationCount = 0,
                    LastLocalizationConfidence = confidence,
                    LastCandidateMargin = margin,
                    LastValidatedAt = DateTimeOffset.UtcNow
                },
                candidateMargin: margin);
            StageAutomaticMapCacheEntry(confirmedEntry);
            await UpsertMapCacheAsync(confirmedEntry);
            return;
        }
    }
}
