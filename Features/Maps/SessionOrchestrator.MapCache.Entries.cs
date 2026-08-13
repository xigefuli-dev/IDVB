namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task UpsertMapCacheAsync(MapFeatureCacheEntry entry)
    {
        await _mapCacheWriteGate.WaitAsync();
        try
        {
            await _mapFeatureCacheRepository.UpsertAsync(entry);
        }
        finally
        {
            _mapCacheWriteGate.Release();
        }
    }

    private async Task DrainMapCacheWritesAsync()
    {
        await _mapCacheWriteGate.WaitAsync();
        _mapCacheWriteGate.Release();
    }

    private void ResetAutomaticMapCacheSamples()
    {
        lock (_automaticMapCacheGate)
        {
            _automaticMapCacheSamples.Clear();
            _pendingAutomaticMapCacheEntries.Clear();
        }
        _lastAlignmentResolution = null;
        _lastAlignmentObservedDpi = 0;
        _hasCompletedQuickScanAlignment = false;
    }

    private void DiscardAutomaticMapCacheSamples(string reason)
    {
        int groups;
        int samples;
        int stagedEntries;
        lock (_automaticMapCacheGate)
        {
            groups = _automaticMapCacheSamples.Count;
            samples = _automaticMapCacheSamples.Sum(pair => pair.Value.Count);
            stagedEntries = _pendingAutomaticMapCacheEntries.Count;
            _automaticMapCacheSamples.Clear();
            _pendingAutomaticMapCacheEntries.Clear();
        }
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"本局自动地图缓存样本已丢弃 · groups={groups} · "
            + $"samples={samples} · staged={stagedEntries} · reason={reason}");
    }

    private void StageAutomaticMapCacheEntry(MapFeatureCacheEntry entry)
    {
        lock (_automaticMapCacheGate)
        {
            if (!_pendingAutomaticMapCacheEntries.TryGetValue(
                    entry.Key,
                    out var existing)
                || AutomaticStagePriority(entry.Scale.Source)
                    > AutomaticStagePriority(existing.Scale.Source)
                || entry.Scale.Source == existing.Scale.Source
                    && entry.Scale.Confidence > existing.Scale.Confidence)
            {
                _pendingAutomaticMapCacheEntries[entry.Key] = entry;
            }
        }
    }

    private static int AutomaticStagePriority(MapFeatureCacheSource source) =>
        source switch
        {
            MapFeatureCacheSource.Recovery => 4,
            MapFeatureCacheSource.CrossResolutionValidated => 3,
            MapFeatureCacheSource.PreprocessedEstimate => 2,
            MapFeatureCacheSource.Automatic => 1,
            _ => 0
        };

    private static bool TryGetUniformScale(
        MapOverlayTransform transform,
        out double scale)
    {
        scale = (transform.ScaleX + transform.ScaleY) / 2d;
        return double.IsFinite(scale)
            && scale > 0.05d
            && Math.Abs(transform.ScaleX - transform.ScaleY) / scale <= 0.01d;
    }

    private static MapFeatureCacheEntry CreateCacheEntry(
        MapFeatureCacheKey key,
        double scale,
        MapFeatureCacheSource source,
        int sampleCount,
        double confidence,
        double relativeMad,
        uint observedDpi,
        MapScaleEstimationEvidence? estimationEvidence = null,
        MapScaleCacheValidationMetadata? validation = null,
        double candidateMargin = 1d)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        return new MapFeatureCacheEntry
        {
            Key = key,
            Scale = new MapScaleCachePayload
            {
                UniformScale = scale,
                Source = source,
                SampleCount = sampleCount,
                Confidence = Math.Clamp(confidence, 0d, 1d),
                RelativeMedianAbsoluteDeviation = Math.Max(0d, relativeMad),
                LastObservedDpi = observedDpi,
                EstimationEvidence = estimationEvidence,
                Validation = validation ?? new MapScaleCacheValidationMetadata
                {
                    DirectlyTrusted = source is MapFeatureCacheSource.Manual
                        or MapFeatureCacheSource.Player,
                    SuccessfulValidationCount = source is
                        MapFeatureCacheSource.Manual or MapFeatureCacheSource.Player
                            ? 0
                            : sampleCount,
                    LastLocalizationConfidence =
                        Math.Clamp(confidence, 0d, 1d),
                    LastCandidateMargin = Math.Clamp(
                        candidateMargin,
                        0d,
                        1d),
                    LastValidatedAt = source is
                        MapFeatureCacheSource.Manual or MapFeatureCacheSource.Player
                        ? default
                        : updatedAt
                },
                UpdatedAt = updatedAt
            }
        };
    }

    /// <summary>
    /// Returns a shallow copy of the cache entry with the validation metadata
    /// replaced. The entry/payload are classes, not records, so the payload
    /// fields are copied individually; estimation evidence is shared by
    /// reference (immutable after write).
    /// </summary>
    private static MapFeatureCacheEntry CopyEntryWithValidation(
        MapFeatureCacheEntry source,
        MapScaleCacheValidationMetadata validation)
    {
        var scale = source.Scale;
        var copiedScale = new MapScaleCachePayload
        {
            SchemaVersion = scale.SchemaVersion,
            UniformScale = scale.UniformScale,
            Source = scale.Source,
            SampleCount = scale.SampleCount,
            Confidence = scale.Confidence,
            RelativeMedianAbsoluteDeviation =
                scale.RelativeMedianAbsoluteDeviation,
            LastObservedDpi = scale.LastObservedDpi,
            EstimationEvidence = scale.EstimationEvidence,
            Validation = validation,
            UpdatedAt = scale.UpdatedAt
        };
        return new MapFeatureCacheEntry
        {
            SchemaVersion = source.SchemaVersion,
            Key = source.Key,
            Scale = copiedScale
        };
    }

    /// <summary>
    /// Returns a copy of the recognition with the overlay transform replaced by
    /// the player-confirmed transform. All other result fields are preserved.
    /// </summary>
    private static RuntimeMapRecognition WithOverlayTransform(
        RuntimeMapRecognition recognition,
        MapOverlayTransform transform)
    {
        var result = recognition.Result;
        return new RuntimeMapRecognition
        {
            Map = recognition.Map,
            FloorImagePath = recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = result.OrientationDegrees,
                Confidence = result.Confidence,
                IdentityConfidence = result.IdentityConfidence,
                LocalizationConfidence = result.LocalizationConfidence,
                Source = result.Source,
                HasAllRequiredAnchorEvidence = result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = transform,
                AnchorMatches = result.AnchorMatches,
                StructureBestScore = result.StructureBestScore,
                StructureSecondScore = result.StructureSecondScore,
                StructureCandidateMargin = result.StructureCandidateMargin,
                StructureRejectionReason = result.StructureRejectionReason,
                WasForcedBestResult = result.WasForcedBestResult,
                ReusedLastTransform = result.ReusedLastTransform,
                UsedCachedScale = result.UsedCachedScale,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation = result.SkippedStructureValidation
            }
        };
    }

    private static RuntimeMapRecognition MarkUsedCachedScale(
        RuntimeMapRecognition recognition)
    {
        var result = recognition.Result;
        return new RuntimeMapRecognition
        {
            Map = recognition.Map,
            FloorImagePath = recognition.FloorImagePath,
            Result = new MapRecognitionResult
            {
                MapId = result.MapId,
                Floor = result.Floor,
                OrientationDegrees = result.OrientationDegrees,
                Confidence = result.Confidence,
                IdentityConfidence = result.IdentityConfidence,
                LocalizationConfidence = result.LocalizationConfidence,
                Source = result.Source,
                HasAllRequiredAnchorEvidence = result.HasAllRequiredAnchorEvidence,
                GeometryMargin = result.GeometryMargin,
                UsedLocalConfirmation = result.UsedLocalConfirmation,
                OverlayTransform = result.OverlayTransform,
                AnchorMatches = result.AnchorMatches,
                StructureBestScore = result.StructureBestScore,
                StructureSecondScore = result.StructureSecondScore,
                StructureCandidateMargin = result.StructureCandidateMargin,
                StructureRejectionReason = result.StructureRejectionReason,
                WasForcedBestResult = result.WasForcedBestResult,
                ReusedLastTransform = result.ReusedLastTransform,
                UsedCachedScale = true,
                EvidenceKind = result.EvidenceKind,
                StructureDisposition = result.StructureDisposition,
                SkippedStructureValidation = result.SkippedStructureValidation
            }
        };
    }

    private static MapRecognitionAttempt CopyAttempt(
        MapRecognitionAttempt source,
        RuntimeMapRecognition recognition) => new()
    {
        Recognition = recognition,
        Choices = source.Choices,
        Diagnostics = source.Diagnostics,
        FailureReason = source.FailureReason,
        StructureResult = source.StructureResult,
        GateDetectionResult = source.GateDetectionResult,
        StructureAttempted = source.StructureAttempted,
        StructureAccepted = source.StructureAccepted,
        StructureFailureReason = source.StructureFailureReason,
        SearchStage = source.SearchStage
    };
}
