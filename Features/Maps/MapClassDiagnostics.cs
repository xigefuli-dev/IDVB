namespace IDVBuff.Features.Maps;

public sealed record MapClassDiagnostic(
    string MapClass,
    bool IsHealthy,
    IReadOnlyList<string> Problems)
{
    public string DisplayName => IsHealthy ? MapClass : $"{MapClass}  ⚠";
    public string Summary => IsHealthy
        ? $"{MapClass} 的地图资源完整。"
        : string.Join(Environment.NewLine, Problems);
}

public sealed class MapClassDiagnosticCoordinator
{
    public static MapClassDiagnosticCoordinator Instance { get; } = new();
    private readonly object _gate = new();
    private IReadOnlyDictionary<string, MapClassDiagnostic> _snapshot =
        new Dictionary<string, MapClassDiagnostic>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _pending;
    private long _generation;
    public event Action? SnapshotChanged;

    private MapClassDiagnosticCoordinator() { }

    public IReadOnlyDictionary<string, MapClassDiagnostic> Snapshot =>
        Volatile.Read(ref _snapshot);

    public void Trigger(MapRepository repository)
    {
        CancellationTokenSource cancellation;
        long generation;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cancellation = new CancellationTokenSource();
            generation = ++_generation;
        }
        _ = Task.Run(() => RunAsync(repository, generation, cancellation.Token));
    }

    private async Task RunAsync(MapRepository repository, long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await repository.HealMissingPrebuiltStructureLinesAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A rejected repair must remain visible in the resource diagnostics.
                System.Diagnostics.Debug.WriteLine($"[MapDiagnostics] 自动修复未完成：{exception}");
            }
            var diagnostics = await repository.DiagnoseMapClassesAsync(cancellationToken)
                .ConfigureAwait(false);
            var next = diagnostics.ToDictionary(
                item => item.MapClass, StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                if (generation == _generation && !cancellationToken.IsCancellationRequested)
                    Volatile.Write(ref _snapshot, next);
                else
                    return;
            }
            SnapshotChanged?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MapDiagnostics] 后台完整性诊断失败：{exception}");
        }
    }
}

public sealed partial class MapRepository
{
    public async Task<IReadOnlyList<MapClassDiagnostic>> DiagnoseMapClassesAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await GetCatalogSnapshotAsync();
        return await Task.Run<IReadOnlyList<MapClassDiagnostic>>(() => catalog.Classes
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(mapClass => DiagnoseMapClass(
                mapClass,
                catalog.Maps.Where(map => string.Equals(
                    map.Class, mapClass, StringComparison.OrdinalIgnoreCase)).ToArray(),
                cancellationToken))
            .ToArray(), cancellationToken);
    }

    private MapClassDiagnostic DiagnoseMapClass(
        string mapClass,
        IReadOnlyList<MapRecord> maps,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (maps.Count == 0)
            problems.Add("没有任何地图。");

        foreach (var map in maps.OrderBy(item => item.SequenceNumber))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var floors = MapFloorRules.GetOrderedFloors(map);
            var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
            if (floors.Count == 0)
            {
                problems.Add($"{map.DisplayName}：没有楼层。");
                continue;
            }

            foreach (var floor in floors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prefix = $"{map.DisplayName} · {floor.DisplayName}";
                CheckImageAsset(problems, prefix, "地图原图", () => GetFloorImagePath(map, floor.Key),
                    floor.ImageFileLength, floor.ImageSha256);
                CheckImageAsset(problems, prefix, "识别图", () => GetFloorRecognitionPath(map, floor.Key),
                    floor.RecognitionFileLength, floor.RecognitionSha256);
                CheckImageAsset(problems, prefix, "叠加图", () => GetFloorOverlayPath(map, floor.Key),
                    floor.OverlayFileLength, floor.OverlaySha256);

                if (floor.EntryIdentityAsset is not null)
                {
                    try
                    {
                        // Entry-backed floors use their authored channels, not legacy scan assets.
                        HasValidatedEntryStructure(map, floor);
                        MapEntryIdentityResources.ValidateFiles(GetMapDirectory(map.Id), floor.EntryIdentityAsset,
                            map.SourcePackageMapId ?? map.Id, floor.Key);
                    }
                    catch (Exception exception)
                    {
                        problems.Add($"{prefix}：入口身份资源不可用（{exception.Message}）");
                    }
                }
                else if (!HasPrebuiltStructureLine(map, floor.Key))
                    problems.Add($"{prefix}：预生成二值轮廓线图缺失、过期或损坏。");
                else
                    ValidatePrebuiltFiles(problems, map, floor, prefix);

                if (floor.EntryIdentityAsset is null && RequiresScanAssets(scanFloorKey, floor.Key))
                {
                    if (!MapScanFloorRules.HasRequiredScanMarkers(map, floor.Key))
                        problems.Add($"{prefix}：缺少完整的扫描门锚点区域。");
                    if (!TryGetValidSideEntranceFeaturePath(
                            map, floor.Key, out _, out var sideEntranceFailure))
                        problems.Add($"{prefix}：侧门特征不可用（{sideEntranceFailure}）");
                }
            }
        }

        return new MapClassDiagnostic(mapClass, problems.Count == 0, problems);
    }

    internal static bool RequiresScanAssets(string scanFloorKey, string floorKey) =>
        string.Equals(
            MapScanFloorRules.NormalizeFloorIdentity(scanFloorKey),
            MapScanFloorRules.NormalizeFloorIdentity(floorKey),
            StringComparison.Ordinal);

    private static void CheckImageAsset(
        ICollection<string> problems,
        string prefix,
        string assetName,
        Func<string> resolvePath,
        long expectedLength,
        string expectedSha256)
    {
        try
        {
            var path = resolvePath();
            if (!File.Exists(path)
                || expectedLength <= 0
                || new FileInfo(path).Length != expectedLength
                || expectedSha256.Length != 64
                || !string.Equals(ComputeFileSha256(path), expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                problems.Add($"{prefix}：{assetName}缺失或完整性校验失败。");
        }
        catch (Exception exception)
        {
            problems.Add($"{prefix}：{assetName}不可用（{exception.Message}）");
        }
    }

    private void ValidatePrebuiltFiles(
        ICollection<string> problems,
        MapRecord map,
        FloorDefinition floor,
        string prefix)
    {
        try
        {
            var asset = floor.PrebuiltStructureLine!;
            var linePath = GetPrebuiltStructureLinePath(map, floor.Key);
            var algorithmPath = GetPrebuiltStructureAlgorithmPath(map, floor.Key);
            if (!string.Equals(ComputeFileSha256(linePath), asset.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                problems.Add($"{prefix}：预生成二值轮廓线图哈希不匹配。");
            if (!File.Exists(algorithmPath)
                || !string.Equals(ComputeFileSha256(algorithmPath), asset.AlgorithmSha256,
                    StringComparison.OrdinalIgnoreCase))
                problems.Add($"{prefix}：预生成轮廓算法文件缺失或哈希不匹配。");
        }
        catch (Exception exception)
        {
            problems.Add($"{prefix}：预生成二值轮廓资源不可用（{exception.Message}）");
        }
    }
}
