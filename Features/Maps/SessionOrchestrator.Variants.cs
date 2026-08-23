namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task<MapVariantSelectionContext?> GetCurrentVariantContextAsync()
    {
        var match = _matchSession.Snapshot;
        if (!match.IsStarted || match.Mode != MapRunMode.Normal)
            return null;

        var selectedMapId = _mapLease.MapId
            ?? _pendingAlignmentIdentity?.Map.Id
            ?? _lastRecognition?.Map.Id;
        if (selectedMapId is not { } mapId)
            return null;

        var catalog = await _mapRepository.GetCatalogSnapshotAsync();
        var group = catalog.VariantGroups.SingleOrDefault(candidate =>
            candidate.MapIds.Contains(mapId));
        if (group is null)
            return null;

        var mapsById = catalog.Maps.ToDictionary(map => map.Id);
        var ordered = group.MapIds
            .Where(mapsById.ContainsKey)
            .Select(id => mapsById[id])
            .OrderBy(map => map.SequenceNumber)
            .ThenBy(map => map.Id)
            .ToArray();
        if (ordered.Length < 2)
            return null;

        var isPending = _pendingAlignmentIdentity?.Map.Id == mapId
            || _lastRecognition?.Map.Id != mapId
            || !_mapOpenSession.Snapshot.IsLocked;
        var options = ordered.Select((map, index) => new MapVariantOption(
            map.Id,
            index + 1,
            map.SequenceNumber,
            map.DisplayName,
            map.Id == mapId,
            map.Id == mapId && isPending)).ToArray();
        return new MapVariantSelectionContext(group.Id, options);
    }

    public async Task SwitchVariantAsync(Guid targetMapId)
    {
        var realignImmediately = false;
        MapGameToggleTransition openTransition = default;
        await _matchLifecycleGate.WaitAsync();
        try
        {
            var previousMatch = _matchSession.Snapshot;
            if (!previousMatch.IsStarted || previousMatch.Mode != MapRunMode.Normal)
                throw new InvalidOperationException("只有普通对局可以切换地图变体。");

            var currentMapId = _mapLease.MapId
                ?? _pendingAlignmentIdentity?.Map.Id
                ?? _lastRecognition?.Map.Id
                ?? throw new InvalidOperationException("当前尚未选择地图。");
            if (targetMapId == currentMapId)
                return;

            var catalog = await _mapRepository.GetCatalogSnapshotAsync();
            var group = catalog.VariantGroups.SingleOrDefault(candidate =>
                candidate.MapIds.Contains(currentMapId));
            if (group is null || !group.MapIds.Contains(targetMapId))
                throw new InvalidOperationException("目标地图已不属于当前变体组合。");
            var currentMap = catalog.Maps.SingleOrDefault(map => map.Id == currentMapId)
                ?? throw new InvalidOperationException("当前地图已从目录中删除。");
            var targetMap = catalog.Maps.SingleOrDefault(map => map.Id == targetMapId)
                ?? throw new InvalidOperationException("目标变体已从目录中删除。");
            if (!string.Equals(currentMap.Class, targetMap.Class, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(targetMap.Class, previousMatch.MapClass, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("变体组合与当前对局 Class 不一致。");
            }

            var currentFloors = MapFloorRules.GetOrderedFloors(currentMap);
            var targetFloors = MapFloorRules.GetOrderedFloors(targetMap);
            if (targetFloors.Count == 0)
                throw new InvalidOperationException("目标变体没有可用楼层。");
            var oldFloor = _currentFloorKey
                ?? _lastRecognition?.Result.Floor
                ?? _pendingAlignmentIdentity?.Result.Floor
                ?? currentFloors.FirstOrDefault()?.Key;
            var floorIndex = Math.Max(0, currentFloors.ToList().FindIndex(floor =>
                string.Equals(floor.Key, oldFloor, StringComparison.Ordinal)));
            var targetFloor = targetFloors.ElementAtOrDefault(floorIndex)?.Key
                ?? targetFloors[0].Key;

            var mapWasOpen = _gameMapToggleState.IsOpen;
            _matchSession.AdvanceOperationEpoch();
            CancelMatchOperations();
            await DrainMatchOperationsAsync();
            await DrainMapCacheWritesAsync();
            DiscardAutomaticMapCacheSamples("切换地图变体，丢弃旧地图尚未落盘的自动缓存样本");
            StartMatchCancellationScope();

            _overlayStatus.Clear();
            _overlay.Clear();
            _overlay.ClearPersistentMiniMap();
            _candidateStability.Reset();
            _alignmentCommitGuard.Invalidate();
            _recognition.ResetMatchState();
            ClearPendingBackgroundScan();
            EndAdaptiveMapOpen("map variant changed");
            ClearAdaptiveSessionKeys();
            ClearMapViewportPresenceReferences();
            lock (_reliableFloorAlignmentGate)
            {
                _reliableFloorAlignments.Clear();
            }
            ClearManualFloorScaleLocks();

            _lastRecognition = null;
            _lastAlignmentSession = null;
            _primaryFloorAlignmentSession = null;
            _pendingAlignmentSeed = null;
            _lastFloorRecognition = null;
            _lastTrustedPlayerPoint = null;
            _alignmentTrackingMode = MapAlignmentTrackingMode.None;
            _currentFloorKey = targetFloor;
            _pendingAlignmentIdentity = CreatePendingVariantIdentity(
                targetMap,
                targetFloor);
            var currentMatch = _matchSession.Snapshot;
            _mapLease.Bind(currentMatch, targetMap.Id);
            _mapOpenSession.BeginVariantChange(targetMap.Id, targetFloor);
            RefreshMiniMapForCurrentFloor();
            _statusMessage = $"已切换到 {targetMap.DisplayName}，等待重新对齐。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"切换地图变体 · from={currentMap.Id} · to={targetMap.Id} · floor={targetFloor}");

            if (mapWasOpen)
            {
                openTransition = _gameMapToggleState.SetOpenForExternalController(true);
                realignImmediately = true;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _matchLifecycleGate.Release();
        }

        if (realignImmediately)
            await RunMapOpenAlignmentAsync(openTransition);
    }

    private RuntimeMapRecognition CreatePendingVariantIdentity(
        MapRecord map,
        string floorKey)
    {
        var floor = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? throw new InvalidOperationException(
                $"地图不包含楼层 '{floorKey}'。");
        return new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = _mapRepository.GetFloorRecognitionPath(map, floorKey),
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floorKey,
                OrientationDegrees = floor.OrientationDegrees,
                Source = MapRecognitionSource.UserConfirmed,
                Confidence = 0d,
                IdentityConfidence = 1d,
                LocalizationConfidence = 0d,
                OverlayTransform = null
            }
        };
    }

    private bool IsPendingVariantAlignment(Guid mapId, string floorKey)
    {
        var session = _mapOpenSession.Snapshot;
        return session.State == MapSessionState.RecalibrationRequired
            && session.RecalibrationReason == MapRecalibrationReason.VariantChanged
            && session.MapId == mapId
            && string.Equals(session.Floor, floorKey, StringComparison.Ordinal)
            && _pendingAlignmentIdentity?.Map.Id == mapId
            && string.Equals(
                _pendingAlignmentIdentity.Result.Floor,
                floorKey,
                StringComparison.Ordinal);
    }

    private void RestorePendingVariantStatusAfterTransient(
        string failureMessage,
        RuntimeMapRecognition identity,
        string floorKey)
    {
        if (!IsPendingVariantAlignment(identity.Map.Id, floorKey))
            return;

        var match = _matchSession.Snapshot;
        var cancellationToken = CurrentMatchCancellationToken;
        _ = RestorePendingVariantStatusAfterTransientAsync(
            failureMessage,
            identity.Map,
            floorKey,
            match,
            cancellationToken);
    }

    private async Task RestorePendingVariantStatusAfterTransientAsync(
        string failureMessage,
        MapRecord map,
        string floorKey,
        MapMatchSnapshot match,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                MapOverlayStatusCoordinator.DefaultTransientLifetime,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        void Restore()
        {
            if (_disposed
                || !_matchSession.IsCurrent(match)
                || !string.Equals(_statusMessage, failureMessage, StringComparison.Ordinal)
                || !IsPendingVariantAlignment(map.Id, floorKey))
            {
                return;
            }

            var floorLabel = MapFloorRules.GetFloorDisplayName(map, floorKey);
            _statusMessage = $"已选择 {map.DisplayName} · {floorLabel}，等待重新对齐。";
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        if (!_dispatcher.TryEnqueue(Restore))
            Restore();
    }
}
