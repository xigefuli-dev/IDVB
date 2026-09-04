using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService : IDisposable
{
    private const int Vpsg3RebuildWorkerCount = 3;

    private readonly Vpsg3PreparedIndexRegistry _vpsg3Registry = new();
    private readonly CancellationTokenSource _vpsg3RebuildCts = new();

    /// <summary>
    /// Application-lifetime registry holding prepared reference floor indices for VPSG 3.0.
    /// </summary>
    public IVpsg3PreparedIndexRegistry Vpsg3Registry => _vpsg3Registry;

    /// <summary>
    /// Synchronously invalidates changed maps in the VPSG3 registry, then triggers
    /// background asynchronous rebuilding for the invalidated floors using bounded parallelism (3 workers).
    /// Alignment never waits for rebuild to complete.
    /// Only floors with complete, valid PrebuiltStructureLine are eligible for VPSG3 index building.
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

        // Collect all eligible prebuilt tasks
        var buildTasks = new List<(MapRecord Map, FloorDefinition Floor, string LinePath, Vpsg3IndexCacheKey CacheKey)>();
        foreach (var map in targets)
        {
            var fingerprint = MapFeatureCacheRules.ComputeContentFingerprint(map);

            foreach (var floor in map.Floors)
            {
                // Strict PrebuiltStructureLine contract: ineligible floors never get a VPSG3 index
                if (!TryGetEligiblePrebuiltPath(map, floor, out var linePath) || linePath is null)
                    continue;

                var structureGen = Vpsg3IndexCacheKey.CreatePrebuiltGenerationIdentity(
                    floor.PrebuiltStructureLine!,
                    schemaVersion: 1);

                var cacheKey = new Vpsg3IndexCacheKey(
                    map.Id,
                    floor.Key,
                    fingerprint,
                    map.UpdatedAt,
                    structureGen,
                    SchemaVersion: 1);

                buildTasks.Add((map, floor, linePath, cacheKey));
            }
        }

        if (buildTasks.Count == 0)
            return;

        // Dispatch background bounded parallel execution (3 workers)
        _ = Task.Run(async () =>
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Vpsg3RebuildWorkerCount,
                CancellationToken = token
            };

            try
            {
                await Parallel.ForEachAsync(buildTasks, options, (taskItem, ct) =>
                {
                    if (ct.IsCancellationRequested)
                        return ValueTask.CompletedTask;

                    // Prevent duplicate parallel builds for the same key
                    if (!_vpsg3Registry.TryBeginBuild(taskItem.CacheKey))
                        return ValueTask.CompletedTask;

                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        using var image = Cv2.ImRead(taskItem.LinePath, ImreadModes.Grayscale);
                        if (image.Empty())
                        {
                            _vpsg3Registry.RecordBuildFailure(taskItem.CacheKey, "Decoded prebuilt line image is empty.");
                            return ValueTask.CompletedTask;
                        }

                        var preparedFloor = Vpsg3PreparedIndexBuilder.BuildFromMat(image, taskItem.CacheKey);
                        _vpsg3Registry.TryPublishFloor(taskItem.CacheKey, preparedFloor);
                    }
                    catch (OperationCanceledException)
                    {
                        // Background task cancelled
                    }
                    catch (Exception ex)
                    {
                        _vpsg3Registry.RecordBuildFailure(taskItem.CacheKey, ex.Message);
                    }

                    return ValueTask.CompletedTask;
                });
            }
            catch (OperationCanceledException)
            {
                // Service being disposed or rebuild cancelled
            }
        }, token);
    }

    private bool TryGetEligiblePrebuiltPath(
        MapRecord map,
        FloorDefinition floor,
        out string? prebuiltPath)
    {
        prebuiltPath = null;
        if (floor.PrebuiltStructureLine?.IsComplete is not true)
            return false;

        if (!string.Equals(
                floor.PrebuiltStructureLine.SourceSha256,
                floor.RecognitionSha256,
                StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            var path = _repository.GetPrebuiltStructureLinePath(map, floor.Key);
            if (File.Exists(path))
            {
                prebuiltPath = path;
                return true;
            }
        }
        catch
        {
            // Corrupted or missing prebuilt line file
        }

        return false;
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
