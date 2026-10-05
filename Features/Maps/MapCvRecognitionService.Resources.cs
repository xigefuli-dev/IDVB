using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private IReadOnlyList<MapRecord> ResidentMaps(IReadOnlyList<MapRecord> maps) =>
        !_matchScopedResources ? maps : string.IsNullOrWhiteSpace(_residentMapClass)
            ? []
            : maps.Where(map => string.Equals(map.Class, _residentMapClass,
                StringComparison.OrdinalIgnoreCase)).ToArray();

    // Called under the orchestrator's lifecycle gate, before publishing a started
    // match. Preparation must not consume the first scan's deadline.
    internal async Task PrepareMatchResourcesAsync(string mapClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapClass);
        if (!_matchScopedResources) return;
        await _cacheGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await _vpsg3Preparation;
            ClearResidentCatalogResources();
            _residentMapClass = mapClass;
            try
            {
                var maps = ResidentMaps(_maps);
                _sideEntranceFeatureCache = await Task.Run(() =>
                {
                    var cache = MapCvRecognitionHelpers.BuildSideEntranceFeatureCache(_repository, maps);
                    if (cache.Count > 0)
                        PrewarmDeepScan(cache.Values);
                    return cache;
                });
                InvalidateAndTriggerVpsg3Rebuild(maps, maps.Select(map => map.Id).ToHashSet());
                await _vpsg3Preparation;
                AuditResidentResources("MatchResources.Prepared");
            }
            catch
            {
                ClearResidentCatalogResources();
                throw;
            }
        }
        finally { _cacheGate.Release(); }
    }

    // The orchestrator has already stopped and drained scans/alignment. Drain
    // preparation as well: a timeout is not proof that its native work stopped.
    internal async Task ReleaseMatchResourcesAsync()
    {
        Task[] pending;
        lock (_floorPrewarmGate)
        {
            _matchCts.Cancel();
            pending = _allFloorPrewarmTasks.ToArray();
        }
        try { await Task.WhenAll(pending); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OutputLog.Write("WARN", "PERF/RESOURCES", $"Floor preparation failed while draining: {ex}");
        }

        await _cacheGate.WaitAsync();
        try
        {
            await _vpsg3Preparation;
            AuditResidentResources("MatchResources.BeforeRelease");
            if (_matchScopedResources)
                ClearResidentCatalogResources();
            else
                ClearPrecisionResources();
            _vpsgScaleGraphCache.Clear();
            AuditResidentResources("MatchResources.Released");
        }
        finally { _cacheGate.Release(); }
    }

    private void ClearPrecisionResources()
    {
        lock (_precisionLruGate)
        {
            foreach (var floor in _activePrecisionFloors)
                floor.EvictPrecisionDistance();
            _activePrecisionFloors.Clear();
        }
    }

    private void ClearResidentCatalogResources()
    {
        _residentMapClass = null;
        ClearPrecisionResources();
        _vpsg3Registry.Clear();
        foreach (var mat in _sideEntranceFeatureCache.Values)
            mat.Dispose();
        _sideEntranceFeatureCache = [];
        _deepScanGateDetector?.Dispose();
        _deepScanGateDetector = null;
    }

    private void AuditResidentResources(string phase)
    {
        long contourBytes = 0, distanceBytes = 0;
        foreach (var mat in _sideEntranceFeatureCache.Values)
        {
            contourBytes += mat.Total() * mat.ElemSize();
            distanceBytes += ScanStructureIndex.Get(mat).RetainedDistanceBytes;
        }
        RealtimePerformanceTracker.AuditLifecycle(phase,
            $"class={_residentMapClass ?? "<idle>"}; scanFloors={_sideEntranceFeatureCache.Count}; "
            + $"contourBytes={contourBytes}; scanDistanceBytes={distanceBytes}; "
            + $"vpsg3Floors={_vpsg3Registry.ReadyCount}; vpsg3Bytes={_vpsg3Registry.TotalMemoryBytes}");
    }
}
