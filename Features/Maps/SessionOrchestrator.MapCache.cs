namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly object _automaticMapCacheGate = new();
    private readonly SemaphoreSlim _mapCacheWriteGate = new(1, 1);
    private readonly Dictionary<MapFeatureCacheKey, List<MapScaleSample>>
        _automaticMapCacheSamples = [];
    private readonly Dictionary<MapFeatureCacheKey, MapFeatureCacheEntry>
        _pendingAutomaticMapCacheEntries = [];
    private MapCacheResolutionSignature? _lastAlignmentResolution;
    private uint _lastAlignmentObservedDpi;
    private bool _hasCompletedQuickScanAlignment;

    private MapCacheResolutionSignature GetResolution(CapturedGameFrame frame) =>
        MapCacheResolutionSignature.FromBounds(
            frame.ClientBounds,
            frame.ViewportBounds,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle));

    private MapRecognitionAttempt AlignUsingScaleCache(
        CapturedGameFrame frame,
        MapRecord map,
        string floorKey,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        Func<MapRecognitionAttempt> fallback,
        out MapFeatureCacheKey? repairKey)
    {
        repairKey = null;
        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return fallback();

        var key = MapFeatureCacheRules.CreateKey(map, floorKey, resolution);
        if (!_mapFeatureCacheRepository.TryGet(key, out var entry)
            || entry is null)
        {
            return fallback();
        }

        var cachedAttempt = _recognition.AlignWithCachedScale(
            frame,
            map.Id,
            floorKey,
            MapFeatureCacheRules.CreateScaleSeed(
                map,
                floorKey,
                entry.Scale.UniformScale),
            _settings!.OverlayAlignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence);
        if (cachedAttempt.Recognition is { } cachedRecognition)
        {
            return CopyAttempt(
                cachedAttempt,
                MarkUsedCachedScale(cachedRecognition));
        }

        repairKey = key;
        return fallback();
    }

    private void RecordSuccessfulAlignment(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        if (IsMatchEnding || !_matchSession.Snapshot.IsStarted)
            return;
        var transform = recognition.Result.OverlayTransform;
        var resolution = GetResolution(frame);
        _lastAlignmentResolution = resolution.IsSupported ? resolution : null;
        _lastAlignmentObservedDpi =
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle);
        if (_settings?.AllowAutomaticMapCache is not true
            || !_hasCompletedQuickScanAlignment
            || !resolution.IsSupported
            || transform is null
            || recognition.Result.ReusedLastTransform
            || recognition.Result.UsedCachedScale
            || recognition.Result.Confidence < _settings.SessionTuning.HighConfidence
            || !TryGetUniformScale(transform, out var scale))
        {
            return;
        }

        var key = MapFeatureCacheRules.CreateKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        lock (_automaticMapCacheGate)
        {
            if (!_automaticMapCacheSamples.TryGetValue(key, out var samples))
            {
                samples = [];
                _automaticMapCacheSamples[key] = samples;
            }
            samples.Add(new MapScaleSample(scale, recognition.Result.Confidence));
        }
    }

    private Task RepairMapCacheAsync(
        MapFeatureCacheKey? key,
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame)
    {
        if (_settings?.AllowAutomaticMapCache is not true
            || key is null
            || recognition.Result.OverlayTransform is not { } transform
            || recognition.Result.ReusedLastTransform
            || recognition.Result.Confidence < _settings.SessionTuning.HighConfidence
            || !TryGetUniformScale(transform, out var scale))
        {
            return Task.CompletedTask;
        }

        StageAutomaticMapCacheEntry(CreateCacheEntry(
            key,
            scale,
            MapFeatureCacheSource.Recovery,
            1,
            recognition.Result.Confidence,
            0d,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle)));
        return Task.CompletedTask;
    }

    private Task PersistPreprocessedScaleAsync(
        RuntimeMapRecognition recognition,
        CapturedGameFrame frame,
        MapScanDiagnostics? diagnostics)
    {
        if (_settings?.AllowAutomaticMapCache is not true
            || diagnostics is not
                {
                    ScaleBootstrapSucceeded: true,
                    ScaleBootstrapValidated: true,
                    StructureAccepted: true
                }
            || diagnostics.ScaleBootstrapUniqueMatches
                < MapVpsgScaleEstimator.MinimumUniqueMatches
            || diagnostics.ScaleBootstrapPairVotes
                < MapVpsgScaleEstimator.MinimumPairVotes
            || diagnostics.ScaleBootstrapConfidence
                < _settings.SessionTuning.HighConfidence
            || recognition.Result.OverlayTransform is not { } transform
            || recognition.Result.ReusedLastTransform
            || !string.Equals(
                _currentFloorKey ?? recognition.Result.Floor,
                recognition.Result.Floor,
                StringComparison.Ordinal)
            || !TryGetUniformScale(transform, out var scale))
        {
            return Task.CompletedTask;
        }

        var resolution = GetResolution(frame);
        if (!resolution.IsSupported)
            return Task.CompletedTask;
        var key = MapFeatureCacheRules.CreateKey(
            recognition.Map,
            recognition.Result.Floor,
            resolution);
        if (_mapFeatureCacheRepository.TryGet(key, out var existing)
            && existing is not null
            && (existing.Scale.Source == MapFeatureCacheSource.Manual
                || existing.Scale.Confidence
                    > diagnostics.ScaleBootstrapConfidence))
        {
            return Task.CompletedTask;
        }

        StageAutomaticMapCacheEntry(CreateCacheEntry(
            key,
            scale,
            MapFeatureCacheSource.PreprocessedEstimate,
            1,
            diagnostics.ScaleBootstrapConfidence,
            diagnostics.ScaleBootstrapRelativeMad,
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
            new MapScaleEstimationEvidence
            {
                UniqueMatches = diagnostics.ScaleBootstrapUniqueMatches,
                PairVotes = diagnostics.ScaleBootstrapPairVotes,
                ResidualPixels = diagnostics.ScaleBootstrapResidualPixels,
                RelativeMedianAbsoluteDeviation =
                    diagnostics.ScaleBootstrapRelativeMad
            }));
        return Task.CompletedTask;
    }

    private async Task SaveCurrentMapCacheAsync()
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            await SaveCurrentMapCacheCoreAsync();
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }

    private async Task SaveCurrentMapCacheCoreAsync()
    {
        if (IsMatchEnding || !_matchSession.Snapshot.IsStarted)
        {
            // The binding is global, but a cache save is match-scoped. Ignore
            // late game/UI key events after exit instead of repeatedly
            // recreating an error status overlay over a finished match.
            return;
        }

        var operationMatch = _matchSession.Snapshot;
        string? failure = null;
        if (_settings is null)
            failure = "地图运行时尚未初始化。";
        else if (!_hasCompletedQuickScanAlignment)
            failure = "请先完成一次快捷扫描并锁定地图。";
        else if (_lastRecognition is not { } recognition
            || recognition.Result.OverlayTransform is not { } transform)
            failure = "请先扫描锁定地图并完成一次对齐。";
        else if (!string.Equals(
            _currentFloorKey ?? recognition.Result.Floor,
            recognition.Result.Floor,
            StringComparison.Ordinal))
            failure = "当前楼层尚未完成对齐。";
        else if (_lastAlignmentResolution is not { IsSupported: true } resolution)
            failure = "当前分辨率不支持地图缓存。";
        else if (!TryGetUniformScale(transform, out var scale))
            failure = "本次对齐没有可保存的统一缩放值。";
        else
        {
            var key = MapFeatureCacheRules.CreateKey(
                recognition.Map,
                recognition.Result.Floor,
                resolution);
            try
            {
                await UpsertMapCacheAsync(CreateCacheEntry(
                    key,
                    scale,
                    MapFeatureCacheSource.Manual,
                    1,
                    recognition.Result.Confidence,
                    0d,
                    _lastAlignmentObservedDpi));
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                _statusMessage = "地图缩放缓存已保存。";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    $"手动地图缓存已保存 · map={key.MapId} · "
                    + $"floor={key.FloorKey} · scale={scale:F6}");
                ShowCacheBindingStatus(
                    MapOverlayStatusLevel.Success,
                    "地图缓存已保存",
                    $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}");
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            catch (Exception ex)
            {
                failure = $"写入缓存文件失败：{ex.Message}";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Error,
                    $"手动地图缓存保存失败 · map={key.MapId} · "
                    + $"floor={key.FloorKey} · {ex.Message}",
                    details: new()
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["stackTrace"] = ex.ToString()
                    });
            }
        }

        if (!IsCurrentMatchOperation(operationMatch))
            return;
        _statusMessage = $"地图缓存保存失败：{failure}";
        ShowCacheBindingStatus(
            MapOverlayStatusLevel.Failure,
            "地图缓存保存失败",
            failure!);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowCacheBindingStatus(
        MapOverlayStatusLevel level,
        string title,
        string message)
    {
        if (!_lastGameBounds.IsValid || _lastGameWindowHandle == IntPtr.Zero)
            return;
        ShowTransientOverlayStatus(
            level,
            title,
            message,
            string.Empty,
            _lastGameBounds,
            _lastGameWindowHandle);
    }

    private async Task FlushAutomaticMapCacheAsync()
    {
        Dictionary<MapFeatureCacheKey, MapScaleSample[]> snapshot;
        Dictionary<MapFeatureCacheKey, MapFeatureCacheEntry> pendingEntries;
        lock (_automaticMapCacheGate)
        {
            snapshot = _automaticMapCacheSamples.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray());
            pendingEntries = new(_pendingAutomaticMapCacheEntries);
        }

        if (_settings?.AllowAutomaticMapCache is not true)
        {
            ResetAutomaticMapCacheSamples();
            return;
        }

        var saved = 0;
        var unstable = 0;
        var failed = 0;
        var skippedManual = 0;
        var entriesToPersist = new Dictionary<MapFeatureCacheKey, MapFeatureCacheEntry>(
            pendingEntries);
        foreach (var (key, samples) in snapshot)
        {
            if (!MapScaleSampleAggregator.TryAggregate(samples, out var aggregate)
                || aggregate is null)
            {
                unstable++;
                continue;
            }
            entriesToPersist[key] = CreateCacheEntry(
                key,
                aggregate.Scale,
                MapFeatureCacheSource.Automatic,
                aggregate.SampleCount,
                aggregate.Confidence,
                aggregate.RelativeMedianAbsoluteDeviation,
                _lastAlignmentObservedDpi);
        }

        foreach (var (key, entry) in entriesToPersist)
        {
            // An explicit manual save is always authoritative. Automatic
            // confirmation may update earlier automatic/recovery estimates,
            // but it must never silently replace a value the user saved.
            if (_mapFeatureCacheRepository.TryGet(key, out var existing)
                && existing?.Scale.Source == MapFeatureCacheSource.Manual)
            {
                skippedManual++;
                continue;
            }
            try
            {
                await UpsertMapCacheAsync(entry);
                saved++;
            }
            catch (Exception ex)
            {
                failed++;
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Error,
                    $"自动地图缓存保存失败 · map={key.MapId} · "
                    + $"floor={key.FloorKey} · {ex.Message}",
                    details: new()
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["stackTrace"] = ex.ToString()
                    });
            }
        }

        lock (_automaticMapCacheGate)
        {
            _automaticMapCacheSamples.Clear();
            _pendingAutomaticMapCacheEntries.Clear();
        }
        _logCollector.Append(
            MapLogCategory.Session,
            failed == 0 ? MapLogLevel.Info : MapLogLevel.Warning,
            $"本局自动地图缓存落盘完成 · saved={saved} · "
            + $"unstable={unstable} · staged={pendingEntries.Count} · "
            + $"skippedManual={skippedManual} · failed={failed} · "
            + $"groups={snapshot.Count}");
    }

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
                || entry.Scale.Source == MapFeatureCacheSource.Recovery
                    && existing.Scale.Source != MapFeatureCacheSource.Recovery
                || entry.Scale.Source == existing.Scale.Source
                    && entry.Scale.Confidence > existing.Scale.Confidence)
            {
                _pendingAutomaticMapCacheEntries[entry.Key] = entry;
            }
        }
    }

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
        MapScaleEstimationEvidence? estimationEvidence = null) => new()
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
            UpdatedAt = DateTimeOffset.UtcNow
        }
    };

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
