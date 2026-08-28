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
            || diagnostics.LowStructureEvidenceCount < 1
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
        if (_mapFeatureCacheRepository.TryGet(key, out var existing)
            && existing is not null)
        {
            if (existing.Scale.Source is MapFeatureCacheSource.Manual
                or MapFeatureCacheSource.Player)
            {
                return;
            }

            var existingTrust = existing.Scale.Validation?
                .LowStructureTrustLevel
                ?? LowStructureCacheTrustLevel.None;
            var relativeDifference = Math.Abs(
                existing.Scale.UniformScale - scale)
                / Math.Max(existing.Scale.UniformScale, scale);
            var staleSelfConfirmedEntry =
                existing.Scale.Source == MapFeatureCacheSource.Automatic
                && existing.Scale.Validation?.DirectlyTrusted != true;
            var lockRelativeTolerance = Math.Min(
                tuning.LowStructureScaleConsistencyTolerance,
                LowStructureScaleEvidenceRules.MaximumLockRelativeDifference);
            if (existingTrust == LowStructureCacheTrustLevel.Isolated
                || staleSelfConfirmedEntry
                || relativeDifference
                    > lockRelativeTolerance)
            {
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    relativeDifference > lockRelativeTolerance
                        ? MapLogLevel.Warning
                        : MapLogLevel.Info,
                    $"低结构旧缓存已由独立尺度搜索重新建档 · floor={key.FloorKey}",
                    details: new()
                    {
                        ["mapId"] = key.MapId,
                        ["floor"] = key.FloorKey,
                        ["existingScale"] = existing.Scale.UniformScale,
                        ["newScale"] = scale,
                        ["relativeDifference"] = relativeDifference,
                        ["cacheDecision"] = staleSelfConfirmedEntry
                            ? relativeDifference
                                > lockRelativeTolerance
                                ? "replace-self-confirmed-scale"
                                : "revalidate-self-confirmed-scale"
                            : "replace-disagreed-independent-scale"
                    });
                existing = null;
            }
            else
            {
                var previousCount = Math.Max(1, existing.Scale.SampleCount);
                scale = ((existing.Scale.UniformScale * previousCount) + scale)
                    / (previousCount + 1d);
                var successfulCount = (existing.Scale.Validation?
                    .SuccessfulValidationCount ?? 0) + 1;
                var trust = successfulCount >= Math.Max(
                    tuning.LowStructureCacheConfirmationCount,
                    LowStructureScaleEvidenceRules
                        .MinimumIndependentScaleConfirmations)
                    ? LowStructureCacheTrustLevel.Trusted
                    : LowStructureCacheTrustLevel.Provisional;
                var confirmedEntry = CreateCacheEntry(
                    key,
                    scale,
                    MapFeatureCacheSource.Recovery,
                    sampleCount: previousCount + 1,
                    Math.Max(confidence, existing.Scale.Confidence),
                    relativeMad: 0d,
                    DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
                    validation: new MapScaleCacheValidationMetadata
                    {
                        DirectlyTrusted = false,
                        LowStructureTrustLevel = trust,
                        SuccessfulValidationCount = successfulCount,
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

        var entry = CreateCacheEntry(
            key,
            scale,
            MapFeatureCacheSource.Recovery,
            sampleCount: 1,
            confidence,
            relativeMad: 0d,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
            validation: new MapScaleCacheValidationMetadata
            {
                DirectlyTrusted = false,
                LowStructureTrustLevel = LowStructureCacheTrustLevel.Provisional,
                SuccessfulValidationCount = 1,
                FailedValidationCount = 0,
                LastLocalizationConfidence = confidence,
                LastCandidateMargin = margin,
                LastValidatedAt = DateTimeOffset.UtcNow
            },
            candidateMargin: margin);
        StageAutomaticMapCacheEntry(entry);
        await UpsertMapCacheAsync(entry);
    }
}
