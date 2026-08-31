namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task ResetLockedMapAlignmentEvidenceAsync(
        RuntimeMapRecognition identity,
        string floorKey)
    {
        var match = _matchSession.Snapshot;
        if (!match.IsStarted)
            return;

        await _scanGate.WaitAsync();
        try
        {
            if (!IsCurrentMatchOperation(match))
                return;
            var currentIdentity = _pendingAlignmentIdentity ?? _lastRecognition;
            if (currentIdentity?.Map.Id != identity.Map.Id)
                return;

            var normalizedFloor = AdaptiveScaleAlignment.AdaptiveScaleKey
                .NormalizeFloor(floorKey);
            var contentFingerprint = MapFeatureCacheRules
                .ComputeContentFingerprint(identity.Map);
            ClearRuntimeAlignmentEvidence(
                match,
                identity.Map,
                normalizedFloor,
                contentFingerprint);

            var identityConfidence = Math.Clamp(
                Math.Max(
                    _mapOpenSession.Snapshot.Confidence,
                    Math.Max(
                        identity.Result.IdentityConfidence,
                        identity.Result.Confidence)),
                0d,
                1d);
            var identityOnly = CreateAlignmentResetIdentity(
                identity,
                normalizedFloor,
                identityConfidence);
            _lastRecognition = identityOnly;
            _pendingAlignmentIdentity = identityOnly;
            _pendingAlignmentSeed = null;
            _mapOpenSession.LockMapIdentity(
                identity.Map.Id,
                normalizedFloor,
                identityConfidence);
            _mapLease.Bind(match, identity.Map.Id);

            var adaptiveRemoved = 0;
            var featureCacheRemoved = 0;
            try
            {
                adaptiveRemoved = await _adaptiveScale
                    .ResetMapFloorForPlayerAsync(
                        identity.Map.Id,
                        identity.Map.UpdatedAt,
                        normalizedFloor);
            }
            catch (Exception exception)
            {
                LogAlignmentResetPersistenceFailure(
                    "五次尺度锁",
                    identity.Map.Id,
                    normalizedFloor,
                    exception);
            }
            try
            {
                featureCacheRemoved = await _mapFeatureCacheRepository
                    .RemoveMapFloorAsync(
                        identity.Map.Id,
                        contentFingerprint,
                        normalizedFloor);
            }
            catch (Exception exception)
            {
                LogAlignmentResetPersistenceFailure(
                    "地图尺度缓存",
                    identity.Map.Id,
                    normalizedFloor,
                    exception);
            }

            _statusMessage =
                $"已保留地图：{identity.Map.DisplayName}；已重置 {normalizedFloor.ToUpperInvariant()} 对齐证据。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"玩家重置当前楼层对齐证据 · map={identity.Map.Id} · floor={normalizedFloor}",
                details: new()
                {
                    ["mapId"] = identity.Map.Id,
                    ["floor"] = normalizedFloor,
                    ["identityPreserved"] = true,
                    ["adaptiveEntriesRemoved"] = adaptiveRemoved,
                    ["featureCacheEntriesRemoved"] = featureCacheRemoved
                });
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private void ClearRuntimeAlignmentEvidence(
        MapMatchSnapshot match,
        MapRecord map,
        string floorKey,
        string contentFingerprint)
    {
        _recognition.ResetMatchState();
        _alignmentCommitGuard.Invalidate();
        _lowStructureRecoveryCursor.Reset();
        _lastDiagnostics = null;

        if (MatchesMapFloor(_lastAlignmentSession, map, floorKey))
            _lastAlignmentSession = null;
        if (MatchesMapFloor(_primaryFloorAlignmentSession, map, floorKey))
            _primaryFloorAlignmentSession = null;
        if (_lastReliableAdaptiveKey is { } lastKey
            && MatchesMapFloor(lastKey, map, floorKey))
        {
            _lastReliableAdaptiveKey = null;
        }
        if (_primaryFloorAdaptiveKey is { } primaryKey
            && MatchesMapFloor(primaryKey, map, floorKey))
        {
            _primaryFloorAdaptiveKey = null;
        }

        lock (_reliableFloorAlignmentGate)
        {
            foreach (var key in _reliableFloorAlignments.Keys.Where(key =>
                key.MatchId == match.MatchId
                && key.MapId == map.Id
                && key.MapUpdatedAt == map.UpdatedAt
                && key.FloorKey == floorKey).ToArray())
            {
                _reliableFloorAlignments.Remove(key);
            }
        }
        lock (_manualFloorScaleLockGate)
        {
            foreach (var key in _manualFloorScaleLocks.Keys.Where(key =>
                key.MatchId == match.MatchId
                && key.MapId == map.Id
                && key.MapUpdatedAtTicks == map.UpdatedAt.UtcTicks
                && key.FloorKey == floorKey).ToArray())
            {
                _manualFloorScaleLocks.Remove(key);
            }
        }
        lock (_automaticMapCacheGate)
        {
            bool MatchesCache(MapFeatureCacheKey key) =>
                key.MapId == map.Id
                && key.MapContentFingerprint == contentFingerprint
                && key.FloorKey == floorKey;
            foreach (var key in _automaticMapCacheSamples.Keys
                .Where(MatchesCache).ToArray())
            {
                _automaticMapCacheSamples.Remove(key);
            }
            foreach (var key in _mapCacheRepairSamples.Keys
                .Where(MatchesCache).ToArray())
            {
                _mapCacheRepairSamples.Remove(key);
            }
            _mapCacheRepairPendingKeys.RemoveWhere(key => MatchesCache(key));
            foreach (var key in _pendingAutomaticMapCacheEntries.Keys
                .Where(MatchesCache).ToArray())
            {
                _pendingAutomaticMapCacheEntries.Remove(key);
            }
        }
        lock (_mapViewportReferenceGate)
        {
            _mapViewportReferences.Remove(new MapViewportReferenceKey(
                map.Id,
                NormalizeMapViewportFloorKey(floorKey)));
        }
    }

    private RuntimeMapRecognition CreateAlignmentResetIdentity(
        RuntimeMapRecognition previous,
        string floorKey,
        double identityConfidence)
    {
        var floor = MapFloorRules.GetFloorProfile(previous.Map, floorKey)
            ?? throw new InvalidOperationException(
                $"地图不包含楼层 '{floorKey}'。");
        return new RuntimeMapRecognition
        {
            Map = previous.Map,
            FloorImagePath = _mapRepository.GetFloorOverlayPath(
                previous.Map,
                floorKey),
            Result = new MapRecognitionResult
            {
                MapId = previous.Map.Id,
                Floor = floorKey,
                OrientationDegrees = floor.OrientationDegrees,
                Source = previous.Result.Source,
                Confidence = previous.Result.Confidence,
                IdentityConfidence = identityConfidence,
                LocalizationConfidence = 0d,
                OverlayTransform = null
            }
        };
    }

    private static bool MatchesMapFloor(
        MapAlignmentSession? session,
        MapRecord map,
        string floorKey) =>
        session is not null
        && session.MapId == map.Id
        && session.MapUpdatedAt == map.UpdatedAt
        && session.FloorKey == floorKey;

    private static bool MatchesMapFloor(
        AdaptiveScaleAlignment.AdaptiveScaleKey key,
        MapRecord map,
        string floorKey) =>
        key.MapId == map.Id
        && key.MapUpdatedAtTicks == map.UpdatedAt.UtcTicks
        && key.FloorKey == floorKey;

    private void LogAlignmentResetPersistenceFailure(
        string target,
        Guid mapId,
        string floorKey,
        Exception exception) =>
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Warning,
            $"玩家重置{target}落盘失败 · map={mapId} · floor={floorKey}",
            details: new()
            {
                ["exception"] = exception.GetBaseException().Message
            });
}
