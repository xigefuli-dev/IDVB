namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public int RegisteredEntryMapCount => _recognition.RegisteredEntryMapCount;

    private bool CanPublishEntryAlignment(RuntimeMapRecognition? aligned, CapturedGameFrame frame,
        MapCatalogRevision? revision) => revision is null || aligned is not null
            && IsCurrentCaptureTarget(frame)
            && revision.Value.Equals(_recognition.CatalogRevision)
            && revision.Value.Equals(_mapRepository.GetCatalogRevision())
            && _recognition.TryGetMap(aligned.Map.Id)?.UpdatedAt == aligned.Map.UpdatedAt;

    private AutoFloorCapture? CreateAutomaticIdentityFloorCapture(string? mapClass)
    {
        if (_settings?.DisableAutoFloor == true || string.IsNullOrWhiteSpace(mapClass)) return null;
        var template = _recognition.GetAutomaticIdentityFloorTemplate(mapClass);
        if (template is null) return null;
        var group = FloorIndicatorTemplateRegistry.Resolve(MapFloorRules.GetOrderedFloors(template).Select(f => f.Key));
        // This map supplies only the shared UI layout; it is never selected or
        // used as the identity, pose, scale or floor-answer of the observation.
        return group is null ? null : new AutoFloorCapture(template, group);
    }

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
        if (_recognition.HasEntryIdentityResources(match.MapClass) && frame.DetectedFloorKey is null)
        {
            _logCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
                "入口身份识别等待本帧明确楼层；未使用历史楼层代替当前观察。");
            return null;
        }
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
        if (_recognition.HasEntryIdentityResources(match.MapClass))
        {
            var entry = await Task.Run(() => _recognition.IdentifyEntryMap(frame,
                match.MapClass, floor, token, () => execution.CanCompute), token);
            _logCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                $"入口结构身份识别 · {entry.Reason}", elapsedMs: entry.ElapsedMilliseconds,
                details: new() { ["mapId"] = entry.Map?.Id, ["floor"] = floor,
                    ["competitionComplete"] = entry.CompetitionComplete, ["evidence"] = entry.Evidence });
            return entry.Map is null ? null : CommitObservedIdentity(entry.Map, floor,
                entry.Confidence, frame, match, toggle, generation, execution, token);
        }
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
        return decision.Identity is not { } candidate ? null : CommitObservedIdentity(candidate.Map,
            candidate.FloorKey, decision.StructureSupport, frame, match, toggle, generation, execution, token);
    }

    private RuntimeMapRecognition? CommitObservedIdentity(MapRecord map, string floor, double confidence,
        CapturedGameFrame frame, MapMatchSnapshot match, MapGameToggleTransition toggle,
        long generation, ScanExecutionContext execution, CancellationToken token)
    {
        if (!execution.CanCompute
            || token.IsCancellationRequested || !CanObserveMap
            || !IsMapObservationCurrent(match, toggle, generation) || !IsCurrentCaptureTarget(frame)
            || execution.CatalogRevision?.Equals(_recognition.CatalogRevision) != true
            || execution.CatalogRevision.Equals(_mapRepository.GetCatalogRevision()) != true
            || !string.Equals(map.Class, match.MapClass, StringComparison.OrdinalIgnoreCase)
            || MapFloorRules.GetFloorProfile(map, floor) is null)
            return null;

        // Identity is the only commit here. The fitted feature transform is not
        // a trusted alignment, floor scale, cache entry or tracking seed.
        var selected = new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = _mapRepository.GetFloorOverlayPath(map, floor),
            Result = new MapRecognitionResult
            {
                MapId = map.Id, Floor = floor,
                Confidence = confidence, IdentityConfidence = confidence,
                Source = MapRecognitionSource.Automatic
            }
        };
        execution.CompleteAutomaticPhase();
        return LockSelectedMapIdentity(selected, frame, userConfirmed: false);
    }
}
