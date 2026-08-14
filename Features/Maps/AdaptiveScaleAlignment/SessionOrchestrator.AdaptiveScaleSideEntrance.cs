namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private MapRecognitionAttempt AlignSideEntranceWithScaleFallback(
        CapturedGameFrame frame,
        SideEntranceScanCandidate candidate,
        MapAlignmentSession templateSeed,
        MapRecognitionTuning alignmentTuning,
        MapStructureRegistrationTuning structureTuning,
        out MapAlignmentSession usedSeed)
    {
        usedSeed = templateSeed;
        var targetResolution = GetResolution(frame);
        var rejectionChain = new List<string>();
        var hasAdaptiveSeed = TryAlignWithAdaptiveCalibrationSeed(
            frame,
            candidate.Map,
            candidate.FloorKey,
            _settings!.OverlayAlignmentMode,
            alignmentTuning,
            structureTuning,
            candidate.MatchScore,
            out var adaptiveSeed,
            out var adaptiveAttempt);
        if (hasAdaptiveSeed && adaptiveSeed is not null)
        {
            var adaptiveSession = templateSeed.WithUniformScale(adaptiveSeed.Scale);
            LogScaleSeedDecision(
                candidate,
                adaptiveSeed.Source == AdaptiveScaleAlignment.AdaptiveScaleSeedSource.Runtime
                    ? "adaptive-runtime"
                    : "adaptive-calibration",
                adaptiveSeed.Scale,
                null,
                targetResolution,
                adaptiveAttempt,
                string.Empty);
            if (adaptiveAttempt?.StructureAccepted == true
                && adaptiveAttempt.Recognition is not null
                && IsAdaptiveInitialScaleQualified(adaptiveAttempt, structureTuning))
            {
                usedSeed = adaptiveSession;
                return adaptiveAttempt;
            }
            rejectionChain.Add(
                adaptiveAttempt is null
                    ? "adaptive:unavailable"
                    : $"adaptive:{DescribeAttemptFailure(adaptiveAttempt)}");
        }

        ResolvedMapScaleSeed? cacheSeed = null;
        var cacheRejection = targetResolution.IsSupported
            ? string.Empty
            : "unsupported-target-resolution";
        if (!hasAdaptiveSeed && targetResolution.IsSupported)
        {
            var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(candidate.Map);
            var entries = _mapFeatureCacheRepository.GetSnapshot(
                candidate.Map.Id,
                fingerprint,
                candidate.FloorKey);
            MapScaleSeedResolver.TryResolve(
                entries,
                candidate.Map.Id,
                fingerprint,
                candidate.FloorKey,
                targetResolution,
                _settings.SessionTuning.HighConfidence,
                structureTuning.MinimumCandidateMargin,
                out cacheSeed,
                out cacheRejection);
        }

        if (cacheSeed is not null)
        {
            var projectedSeed = templateSeed.WithUniformScale(cacheSeed.Scale);
            var cacheAttempt = AlignSideEntranceFromSeed(
                frame,
                candidate,
                projectedSeed,
                alignmentTuning,
                structureTuning);
            SetScaleSeedDiagnostics(cacheAttempt, cacheSeed, cacheRejection);
            LogScaleSeedDecision(
                candidate,
                cacheSeed.Source == MapScaleSeedSource.ExactCache
                    ? "exact-cache"
                    : "cross-resolution",
                cacheSeed.Scale,
                cacheSeed.SourceResolution,
                targetResolution,
                cacheAttempt,
                cacheRejection);
            if (cacheAttempt.StructureAccepted
                && cacheAttempt.Recognition is { } cacheRecognition
                && IsAdaptiveInitialScaleQualified(cacheAttempt, structureTuning))
            {
                usedSeed = projectedSeed;
                cacheAttempt = CopyAttempt(
                    cacheAttempt,
                    MarkUsedCachedScale(cacheRecognition));
                if (cacheSeed.IsProjected)
                    StageCrossResolutionValidatedScale(
                        frame,
                        candidate,
                        targetResolution,
                        cacheAttempt);
                return cacheAttempt;
            }
            rejectionChain.Add(
                $"{ScaleSeedSourceName(cacheSeed.Source)}:{DescribeAttemptFailure(cacheAttempt)}");
        }
        else if (!hasAdaptiveSeed)
        {
            rejectionChain.Add($"cache:{cacheRejection}");
            LogScaleSeedDecision(
                candidate,
                "cache-rejected",
                double.NaN,
                null,
                targetResolution,
                null,
                cacheRejection);
        }

        var strictVpsgTuning = MapScaleSeedResolver
            .CreateStrictVpsgValidationTuning(structureTuning);
        var vpsgAttempt = _recognition.AlignLockedFloorFeature(
            frame,
            candidate.Map.Id,
            candidate.FloorKey,
            templateSeed.LockedTransform,
            _settings.OverlayAlignmentMode,
            alignmentTuning,
            strictVpsgTuning,
            candidate.MatchScore);
        SetScaleSeedDiagnostics(
            vpsgAttempt,
            MapScaleSeedSource.Vpsg,
            vpsgAttempt.Diagnostics.ScaleBootstrapScale,
            cacheSeed?.SourceResolution,
            targetResolution,
            string.Join(";", rejectionChain),
            cacheSeed?.CacheSource.ToString() ?? string.Empty,
            cacheSeed?.IsProjected ?? false,
            cacheSeed is { IsProjected: true } ? cacheSeed.Scale : 0d);
        LogScaleSeedDecision(
            candidate,
            "vpsg",
            vpsgAttempt.Diagnostics.ScaleBootstrapScale,
            null,
            targetResolution,
            vpsgAttempt,
            string.Join(";", rejectionChain));
        if (vpsgAttempt.StructureAccepted
            && vpsgAttempt.Recognition is not null
            && IsAdaptiveInitialScaleQualified(vpsgAttempt, structureTuning))
            return vpsgAttempt;
        rejectionChain.Add($"vpsg:{DescribeAttemptFailure(vpsgAttempt)}");

        var templateAttempt = AlignSideEntranceFromSeed(
            frame,
            candidate,
            templateSeed,
            alignmentTuning,
            structureTuning);
        SetScaleSeedDiagnostics(
            templateAttempt,
            MapScaleSeedSource.SideTemplate,
            templateSeed.LockedTransform.ScaleX,
            cacheSeed?.SourceResolution,
            targetResolution,
            string.Join(";", rejectionChain),
            cacheSeed?.CacheSource.ToString() ?? string.Empty,
            cacheSeed?.IsProjected ?? false,
            cacheSeed is { IsProjected: true } ? cacheSeed.Scale : 0d);
        LogScaleSeedDecision(
            candidate,
            "side-template",
            templateSeed.LockedTransform.ScaleX,
            null,
            targetResolution,
            templateAttempt,
            string.Join(";", rejectionChain));
        return templateAttempt;
    }
}
