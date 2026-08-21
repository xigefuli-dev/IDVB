namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task UpsertMapCacheAsync(
        MapFeatureCacheEntry entry,
        bool requireActiveLease = true)
    {
        if (requireActiveLease && !IsCacheKeyForCurrentLease(entry.Key))
            return;
        await _mapCacheWriteGate.WaitAsync();
        try
        {
            if (requireActiveLease && !IsCacheKeyForCurrentLease(entry.Key))
                return;
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
            _mapCacheRepairSamples.Clear();
            _mapCacheRepairPendingKeys.Clear();
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
            _mapCacheRepairSamples.Clear();
            _mapCacheRepairPendingKeys.Clear();
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
        if (!IsCacheKeyForCurrentLease(entry.Key))
            return;
        lock (_automaticMapCacheGate)
        {
            if (!IsCacheKeyForCurrentLease(entry.Key))
                return;
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

    private bool IsCacheKeyForCurrentLease(MapFeatureCacheKey key)
    {
        var match = _matchSession.Snapshot;
        if (!_mapLease.IsCurrent(match, key.MapId))
            return false;
        var identity = _lastRecognition?.Map.Id == key.MapId
            ? _lastRecognition
            : _pendingAlignmentIdentity?.Map.Id == key.MapId
                ? _pendingAlignmentIdentity
                : null;
        return identity is not null
            && string.Equals(
                key.MapContentFingerprint,
                MapFeatureCacheRules.ComputeContentFingerprint(identity.Map),
                StringComparison.Ordinal)
            && string.Equals(
                key.FloorKey,
                _currentFloorKey ?? identity.Result.Floor,
                StringComparison.Ordinal);
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
/*
 * 文件职责：SessionOrchestrator.MapCache.Entries。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
