using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService : IDisposable
{
    private readonly Vpsg3PreparedIndexRegistry _vpsg3Registry = new();
    private readonly CancellationTokenSource _vpsg3RebuildCts = new();

    /// <summary>
    /// Application-lifetime registry holding prepared reference floor indices for VPSG 3.0.
    /// </summary>
    public IVpsg3PreparedIndexRegistry Vpsg3Registry => _vpsg3Registry;

    /// <summary>
    /// Synchronously invalidates changed maps in the VPSG3 registry, then triggers
    /// background asynchronous rebuilding for the invalidated floors.
    /// Alignment never waits for rebuild to complete.
    /// </summary>
    internal void InvalidateAndTriggerVpsg3Rebuild(
        IReadOnlyList<MapRecord> maps,
        IReadOnlySet<Guid> changedMapIds)
    {
        if (changedMapIds is null || changedMapIds.Count == 0)
            return;

        // 1. Synchronous invalidation in the registry
        _vpsg3Registry.InvalidateMaps(changedMapIds);

        // 2. Asynchronous background rebuild without blocking caller or alignment
        if (_disposed || _vpsg3RebuildCts.IsCancellationRequested)
            return;

        var token = _vpsg3RebuildCts.Token;
        var targets = maps.Where(m => changedMapIds.Contains(m.Id)).ToArray();
        if (targets.Length == 0)
            return;

        _ = Task.Run(() =>
        {
            foreach (var map in targets)
            {
                if (token.IsCancellationRequested)
                    break;

                var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(map);
                var tuning = new MapStructureRegistrationTuning();
                var structureGen = tuning.Generation.CacheFingerprint;

                foreach (var floor in map.Floors)
                {
                    if (token.IsCancellationRequested)
                        break;

                    var cacheKey = new Vpsg3IndexCacheKey(
                        map.Id,
                        floor.Key,
                        fingerprint,
                        map.UpdatedAt,
                        structureGen,
                        SchemaVersion: 1);

                    if (!_vpsg3Registry.TryBeginBuild(cacheKey))
                        continue;

                    try
                    {
                        var floorProfile = MapFloorRules.GetFloorProfile(map, floor.Key);
                        if (floorProfile is null)
                        {
                            _vpsg3Registry.RecordBuildFailure(cacheKey, "Floor profile missing.");
                            continue;
                        }

                        var path = GetAlignmentReferencePath(map, floor.Key, tuning);
                        if (!File.Exists(path))
                        {
                            _vpsg3Registry.RecordBuildFailure(cacheKey, $"Reference image not found: {path}");
                            continue;
                        }

                        using var image = Cv2.ImRead(path, ImreadModes.Grayscale);
                        if (image.Empty())
                        {
                            _vpsg3Registry.RecordBuildFailure(cacheKey, "Decoded image is empty.");
                            continue;
                        }

                        var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(image, cacheKey);
                        _vpsg3Registry.PublishFloor(preparedFloor);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _vpsg3Registry.RecordBuildFailure(cacheKey, ex.Message);
                    }
                }
            }
        }, token);
    }

    private void DisposeVpsg3()
    {
        try
        {
            _vpsg3RebuildCts.Cancel();
            _vpsg3RebuildCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _vpsg3Registry.Dispose();
    }
}
