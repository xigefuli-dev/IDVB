namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private LowStructureEvidenceDecision ObserveLowStructureEvidence(
        MapRecognitionAttempt attempt)
    {
        var accepted = attempt.StructureResult is { Accepted: true }
            && attempt.Recognition?.Result.OverlayTransform is { } transform
            && double.IsFinite(transform.ScaleX)
            && transform.ScaleX > 0d;
        var independentlyEstimated = accepted
            && LowStructureScaleEvidenceRules.IsIndependentScaleRoute(
                attempt.Diagnostics.LowStructureRoute);
        return new(
            accepted,
            independentlyEstimated ? 1 : 0,
            accepted && !independentlyEstimated);
    }

    private readonly record struct LowStructureEvidenceDecision(
        bool Accepted,
        int Count,
        bool Pending);

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
