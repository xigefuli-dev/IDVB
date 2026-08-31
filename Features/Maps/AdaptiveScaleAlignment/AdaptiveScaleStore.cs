using System.Text.Json;

namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed record AdaptiveScaleStoreEntry
{
    public int ScaleEvidenceVersion { get; init; }
    public AdaptiveScaleKey Key { get; init; }
    public double CalibrationScale { get; init; }
    public double Confidence { get; init; }
    public double RelativeMad { get; init; }
    public int DistinctOpenCount { get; init; }
    public DateTimeOffset LastValidatedAt { get; init; }
    public string Source { get; init; } = "InitialFiveStreak";
    public List<AdaptiveScaleInitialSample> InitialSamples { get; init; } = [];
}

internal sealed class AdaptiveScaleStoreDocument
{
    public int SchemaVersion { get; init; } = 2;
    public List<AdaptiveScaleStoreEntry> Entries { get; init; } = [];
}

internal sealed class AdaptiveScaleStore
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly Action<string, Exception?>? _warning;
    private Dictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry> _entries = [];
    private bool _writesDisabledForUnsupportedSchema;

    public AdaptiveScaleStore(
        string? path = null,
        Action<string, Exception?>? warning = null)
    {
        _path = path ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "MapRuntime",
            "adaptive-scale-cache.json");
        _warning = warning;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = await TryLoadAsync(_path, cancellationToken).ConfigureAwait(false);
            if (primary.Status == LoadStatus.Valid)
            {
                await CommitLoadedResultAsync(primary, restorePrimary: false, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            if (primary.Status == LoadStatus.Unsupported)
            {
                _writesDisabledForUnsupportedSchema = true;
                _warning?.Invoke("adaptive scale cache schema is unsupported", null);
                return;
            }
            if (primary.Status == LoadStatus.Missing)
                return;

            _warning?.Invoke("adaptive scale cache is damaged; trying backup", primary.Error);
            var backup = await TryLoadAsync(BackupPath, cancellationToken).ConfigureAwait(false);
            if (backup.Status != LoadStatus.Valid)
            {
                _warning?.Invoke("adaptive scale cache backup is unavailable", backup.Error);
                return;
            }
            await CommitLoadedResultAsync(backup, restorePrimary: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public AdaptiveScaleStoreEntry? TryGet(AdaptiveScaleKey key)
    {
        lock (_stateGate)
            return _entries.TryGetValue(key, out var entry) ? entry : null;
    }

    public async Task<AdaptiveScaleStoreEntry> RecordInitialStreakAsync(
        AdaptiveScaleInitialStreakSnapshot streak,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writesDisabledForUnsupportedSchema)
            {
                throw new InvalidOperationException(
                    "Adaptive scale cache uses a newer unsupported schema.");
            }
            Dictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry> snapshot;
            lock (_stateGate)
                snapshot = new(_entries);
            var entry = new AdaptiveScaleStoreEntry
            {
                ScaleEvidenceVersion = 1,
                Key = streak.Key,
                CalibrationScale = streak.MedianScale,
                Confidence = streak.MinimumConfidence,
                RelativeMad = streak.RelativeMad,
                DistinctOpenCount = streak.ConsecutiveCount,
                LastValidatedAt = streak.LastValidatedAt,
                Source = "InitialFiveStreak",
                InitialSamples = streak.Samples.ToList()
            };
            snapshot[streak.Key] = entry;
            await SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
                _entries = snapshot;
            return entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResetAsync(
        AdaptiveScaleKey key,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writesDisabledForUnsupportedSchema)
            {
                throw new InvalidOperationException(
                    "Adaptive scale cache uses a newer unsupported schema.");
            }
            Dictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry> snapshot;
            lock (_stateGate)
            {
                snapshot = new(_entries);
                snapshot.Remove(key);
                // Runtime correctness must not depend on a successful disk
                // write.  If persistence fails, this process still must not
                // snap the recovered floor back to its stale calibration.
                _entries = snapshot;
            }
            await SaveSnapshotAsync(snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ResetMapFloorAsync(
        Guid mapId,
        long mapUpdatedAtTicks,
        string floorKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writesDisabledForUnsupportedSchema)
            {
                throw new InvalidOperationException(
                    "Adaptive scale cache uses a newer unsupported schema.");
            }
            Dictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry> snapshot;
            int removed;
            lock (_stateGate)
            {
                snapshot = new(_entries);
                var matchingKeys = snapshot.Keys.Where(key =>
                    key.MapId == mapId
                    && key.MapUpdatedAtTicks == mapUpdatedAtTicks
                    && key.FloorKey == floorKey).ToArray();
                foreach (var key in matchingKeys)
                    snapshot.Remove(key);
                removed = matchingKeys.Length;
                _entries = snapshot;
            }
            if (removed > 0)
                await SaveSnapshotAsync(snapshot, cancellationToken)
                    .ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static bool IsTrusted(AdaptiveScaleStoreEntry? entry)
    {
        if (entry is null
            || entry.ScaleEvidenceVersion < 1
            || entry.DistinctOpenCount < 5
            || entry.InitialSamples is not { Count: >= 5 }
            || !double.IsFinite(entry.Confidence)
            || entry.Confidence < 0.82d
            || !double.IsFinite(entry.RelativeMad)
            || entry.RelativeMad > 0.002d
            || !double.IsFinite(entry.CalibrationScale)
            || entry.CalibrationScale <= 0d)
        {
            return false;
        }
        return entry.InitialSamples.TakeLast(5).All(sample =>
            RelativeDifference(sample.Scale, entry.CalibrationScale) <= 0.002d);
    }

    private string BackupPath => _path + ".bak";

    private async Task CommitLoadedResultAsync(
        LoadResult result,
        bool restorePrimary,
        CancellationToken cancellationToken)
    {
        try
        {
            if (restorePrimary || result.Migrated)
                await SaveSnapshotAsync(result.Entries!, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
                _entries = result.Entries!;
            if (restorePrimary)
                _warning?.Invoke("adaptive scale cache restored from backup", null);
            if (result.Migrated)
                _warning?.Invoke("adaptive scale cache migrated to schema 2", null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            if (restorePrimary && !result.Migrated)
            {
                lock (_stateGate)
                    _entries = result.Entries!;
            }
            _warning?.Invoke(
                restorePrimary
                    ? "adaptive scale cache backup loaded; primary restore failed"
                    : "adaptive scale cache migration failed",
                exception);
        }
    }

    private async Task SaveSnapshotAsync(
        IReadOnlyDictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var tempPath = $"{_path}.tmp.{Guid.NewGuid():N}";
        try
        {
            await WriteDocumentAsync(tempPath, entries, cancellationToken).ConfigureAwait(false);
            if (File.Exists(_path))
                File.Replace(tempPath, _path, BackupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, _path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task WriteDocumentAsync(
        string path,
        IReadOnlyDictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry> entries,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            new AdaptiveScaleStoreDocument { Entries = entries.Values.ToList() },
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<LoadResult> TryLoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new(LoadStatus.Missing, null, false, null);
        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<AdaptiveScaleStoreDocument>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document is null || document.SchemaVersion is < 1 or > CurrentSchemaVersion)
                return new(LoadStatus.Unsupported, null, false, null);
            var migrated = document.SchemaVersion == 1;
            var entries = document.Entries
                .Where(IsValidLegacyEntry)
                .Select(entry => migrated
                    ? MigrateSchemaOne(entry)
                    : NormalizeSchemaTwo(entry))
                .GroupBy(entry => entry.Key)
                .ToDictionary(group => group.Key, group => group.MaxBy(item => item.LastValidatedAt)!);
            return new(LoadStatus.Valid, entries, migrated, null);
        }
        catch (Exception exception) when (exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            return new(LoadStatus.Damaged, null, false, exception);
        }
    }

    private static AdaptiveScaleStoreEntry MigrateSchemaOne(AdaptiveScaleStoreEntry entry)
    {
        var trusted = IsSchemaOneTrusted(entry);
        var samples = trusted
            ? Enumerable.Range(0, 5)
                .Select(_ => new AdaptiveScaleInitialSample(
                    entry.CalibrationScale,
                    entry.Confidence,
                    entry.LastValidatedAt))
                .ToList()
            : [];
        return entry with
        {
            DistinctOpenCount = trusted ? 5 : 0,
            RelativeMad = trusted ? Math.Min(entry.RelativeMad, 0.002d) : 0d,
            Source = trusted ? "MigratedTrusted" : "MigratedUntrusted",
            InitialSamples = samples
        };
    }

    private static AdaptiveScaleStoreEntry NormalizeSchemaTwo(
        AdaptiveScaleStoreEntry entry) =>
        entry with
        {
            InitialSamples = (entry.InitialSamples ?? [])
                .Where(sample => double.IsFinite(sample.Scale)
                    && sample.Scale > 0d
                    && double.IsFinite(sample.Confidence))
                .TakeLast(5)
                .ToList()
        };

    private static bool IsSchemaOneTrusted(AdaptiveScaleStoreEntry entry) =>
        entry.DistinctOpenCount >= (entry.Source == "PlayerRepair" ? 5 : 3)
        && entry.Confidence >= (entry.Source is "PlayerRepair" or "ManualRepair" ? 0.90d : 0.82d)
        && entry.RelativeMad <= (entry.Source is "PlayerRepair" or "ManualRepair" ? 0.002d : 0.003d);

    private static bool IsValidLegacyEntry(AdaptiveScaleStoreEntry entry) =>
        entry.Key.MapId != Guid.Empty
        && !string.IsNullOrWhiteSpace(entry.Key.FloorKey)
        && double.IsFinite(entry.CalibrationScale)
        && entry.CalibrationScale >= 0d;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static double RelativeDifference(double left, double right) =>
        Math.Abs(left - right) / Math.Max(Math.Abs(right), 0.000001d);

    private enum LoadStatus { Missing, Valid, Unsupported, Damaged }

    private sealed record LoadResult(
        LoadStatus Status,
        Dictionary<AdaptiveScaleKey, AdaptiveScaleStoreEntry>? Entries,
        bool Migrated,
        Exception? Error);
}
/*
 * 文件职责：AdaptiveScaleStore。
 * 所属模块：Features/Maps，主要负责自适应缩放与楼层独立尺度维护。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
