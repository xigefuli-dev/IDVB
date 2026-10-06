namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    // Geometry/role preparation shares the existing catalog and floor lifecycle.
    // A catalog-only anchor edit must rebind roles even when its raw floor key
    // did not change. Ready indexes avoid constructing a reference Mat on opens;
    // cold callers retain the existing extractor until preparation completes.
    private void ScheduleAutomaticEntryReferencePreparation(IReadOnlyList<MapRecord> maps,
        MapCatalogRevision revision)
    {
        var snapshot = maps.Select(map => map.Clone()).ToArray();
        if (snapshot.Length == 0 || _disposed || _vpsg3RebuildCts.IsCancellationRequested)
            return;
        var floorPreparation = _vpsg3Preparation;
        var token = _vpsg3RebuildCts.Token;
        var preparation = Task.Run(async () =>
        {
            await floorPreparation.ConfigureAwait(false);
            foreach (var map in snapshot)
            foreach (var definition in map.Floors)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || revision != _catalogRevision)
                    return;
                if (!TryGetVpsg3IndexKey(map, definition.Key, out var key)
                    || !_vpsg3Registry.TryGet(key, out var lease) || lease is null)
                    continue;
                using (lease)
                {
                    var floor = lease.Floor;
                    if (floor.AutomaticEntryIndex is { } existing
                        && existing.CacheKey == key && existing.CatalogRevision == revision)
                        continue;
                    try
                    {
                        var anchors = MapFloorRules.GetFloorProfile(map, definition.Key)?.Anchors
                            .Select(anchor => anchor.Clone()).ToArray() ?? [];
                        var result = MapFrontEntryReferenceIndexBuilder.Build(map, definition.Key,
                            floor, key, revision, anchors, token);
                        token.ThrowIfCancellationRequested();
                        if (_disposed || revision != _catalogRevision)
                            return;
                        floor.AutomaticEntryIndex = result.Index;
                        if (!result.IsReady)
                            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                                MapLogLevel.Info, "自动入口参考保留覆盖缺口", details: new()
                                {
                                    ["mapId"] = map.Id, ["floor"] = definition.Key,
                                    ["available"] = result.IsAvailable,
                                    ["issues"] = result.Issues.Select(issue => issue.Code).ToArray()
                                });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        // The new classifier stays unavailable; this does not
                        // falsify readiness of the existing native floor data.
                        floor.AutomaticEntryIndex = null;
                        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                            MapLogLevel.Warning, "自动入口参考准备失败", details: new()
                            {
                                ["mapId"] = map.Id, ["floor"] = definition.Key,
                                ["error"] = exception.ToString()
                            });
                    }
                }
            }
        }, token);
        _vpsg3Preparation = Task.WhenAll(floorPreparation, preparation);
    }
}
