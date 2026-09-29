namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task PrepareAutomaticIdentityAsync(string mapClass)
    {
        try
        {
            if (_recognition.ResolveAutomaticIdentityFloor(mapClass) is { } floor)
                await _recognition.PrepareAutomaticIdentityAsync(mapClass, floor);
        }
        catch (Exception ex)
        {
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                "自动识别参考特征准备失败", details: new() { ["exception"] = ex.ToString() });
        }
    }

    private async Task<RuntimeMapRecognition?> TryIdentifyObservationAsync(CapturedGameFrame frame,
        MapMatchSnapshot match, MapGameToggleTransition toggle, long generation,
        ScanExecutionContext execution, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(match.MapClass)) return null;
        var floor = _recognition.ResolveAutomaticIdentityFloor(match.MapClass, frame.DetectedFloorKey);
        if (floor is null) return null;
        try
        {
            await _recognition.PrepareAutomaticIdentityAsync(match.MapClass, floor).WaitAsync(
                TimeSpan.FromMilliseconds(Math.Max(1, execution.RemainingMilliseconds - 60)), token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // A catalog replacement cancels the old index, not this map-open.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
        {
            // A damaged optional identity index must not stop the independent
            // prebuilt gate route on subsequent observation passes.
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                "整图身份参考不可用，保留结构扫描", details: new() { ["exception"] = ex.ToString() });
            return null;
        }
        if (!execution.CanCompute) return null;
        var decision = await Task.Run(() => _recognition.IdentifyAutomaticMap(frame,
            match.MapClass, floor, token, () => execution.CanCompute), token);
        _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            $"整图身份识别 · {decision.Reason}", elapsedMs: decision.ElapsedMilliseconds,
            details: new()
            {
                ["complete"] = decision.Search.RetrievalCompleted,
                ["floor"] = floor,
                ["mapId"] = decision.Identity?.Map.Id,
                ["support"] = decision.StructureSupport,
                ["structurePoints"] = decision.StructurePoints,
                ["structureComparisons"] = decision.StructureComparisons,
                ["candidates"] = decision.Search.Candidates.Select(c => new
                {
                    mapId = c.Map.Id, c.Map.SequenceNumber, c.FloorKey, c.Matches, c.Inliers, c.AxisAlignedInliers,
                    c.InlierRatio, c.MedianError, c.P90Error, c.HullFraction, c.SpanX, c.SpanY, c.Score
                }).ToArray()
            });
        if (decision.Identity is not { } candidate || !execution.CanCompute
            || token.IsCancellationRequested || !CanObserveMap
            || !IsMapObservationCurrent(match, toggle, generation) || !IsCurrentCaptureTarget(frame)
            || execution.CatalogRevision?.Equals(_recognition.CatalogRevision) != true
            || execution.CatalogRevision.Equals(_mapRepository.GetCatalogRevision()) != true
            || !string.Equals(candidate.Map.Class, match.MapClass, StringComparison.OrdinalIgnoreCase)
            || MapFloorRules.GetFloorProfile(candidate.Map, candidate.FloorKey) is null)
            return null;

        // Identity is the only commit here. The fitted feature transform is not
        // a trusted alignment, floor scale, cache entry or tracking seed.
        var selected = new RuntimeMapRecognition
        {
            Map = candidate.Map,
            FloorImagePath = _mapRepository.GetFloorOverlayPath(candidate.Map, candidate.FloorKey),
            Result = new MapRecognitionResult
            {
                MapId = candidate.Map.Id, Floor = candidate.FloorKey,
                Confidence = decision.StructureSupport, IdentityConfidence = decision.StructureSupport,
                Source = MapRecognitionSource.Automatic
            }
        };
        execution.CompleteAutomaticPhase();
        return LockSelectedMapIdentity(selected, frame, userConfirmed: false);
    }
}
