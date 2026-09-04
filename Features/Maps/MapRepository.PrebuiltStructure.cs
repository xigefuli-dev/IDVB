using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace IDVBuff.Features.Maps;

public sealed record PrebuiltStructureBatchProgress(
    string MapName,
    string FloorName,
    string StageName,
    double CompletedWork,
    double TotalWork,
    int CompletedFloors,
    int TotalFloors);

public sealed record PrebuiltStructureBatchResult(
    string AlgorithmId,
    string AlgorithmDisplayName,
    int MapCount,
    int FloorCount);

public sealed partial class MapRepository
{
    public async Task<PrebuiltStructureBatchResult> GeneratePrebuiltStructureLinesAsync(
        string className,
        string algorithmPath,
        IProgress<PrebuiltStructureBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(algorithmPath, cancellationToken);
        var initial = await GetCatalogSnapshotAsync();
        var selectedMaps = initial.Maps
            .Where(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber)
            .ToArray();
        if (selectedMaps.Length == 0)
            throw new InvalidOperationException("当前地图类没有可处理的地图。");
        await EnsureDerivedAssetsAsync(selectedMaps);
        var refreshed = await GetCatalogSnapshotAsync();
        selectedMaps = refreshed.Maps
            .Where(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase))
            .OrderBy(map => map.SequenceNumber)
            .ToArray();
        var totalFloors = selectedMaps.Sum(map => MapFloorRules.GetOrderedFloors(map).Count);
        var totalWork = Math.Max(1, totalFloors * algorithm.Pipeline.Count);
        var staging = Path.Combine(_rootDirectory, $".prebuilt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var generated = new List<GeneratedPrebuiltLine>();
        try
        {
            var floorOrdinal = 0;
            foreach (var map in selectedMaps)
            {
                foreach (var floor in MapFloorRules.GetOrderedFloors(map))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourcePath = GetFloorRecognitionPath(map, floor.Key);
                    if (!File.Exists(sourcePath))
                        throw new FileNotFoundException($"{map.DisplayName} 的楼层“{floor.DisplayName}”缺少裁剪后图像。", sourcePath);
                    var sourceSha = await ComputeFileSha256Async(sourcePath, cancellationToken);
                    var stagedOutput = Path.Combine(staging, $"{map.Id:N}-{floor.Key}.png");
                    var currentFloor = floorOrdinal;
                    var result = await Task.Run(() => engine.Execute(
                        algorithm,
                        sourcePath,
                        stagedOutput,
                        stage => progress?.Report(new PrebuiltStructureBatchProgress(
                            map.DisplayName,
                            floor.DisplayName,
                            stage.StageName,
                            currentFloor * algorithm.Pipeline.Count
                                + stage.StageIndex
                                + stage.StageFraction,
                            totalWork,
                            currentFloor,
                            totalFloors)),
                        cancellationToken), cancellationToken);
                    var outputSha = await ComputeFileSha256Async(stagedOutput, cancellationToken);
                    generated.Add(new GeneratedPrebuiltLine(
                        map.Id,
                        floor.Key,
                        sourceSha,
                        stagedOutput,
                        outputSha,
                        result.Width,
                        result.Height,
                        new FileInfo(stagedOutput).Length));
                    floorOrdinal++;
                    progress?.Report(new PrebuiltStructureBatchProgress(
                        map.DisplayName,
                        floor.DisplayName,
                        "已完成",
                        floorOrdinal * algorithm.Pipeline.Count,
                        totalWork,
                        floorOrdinal,
                        totalFloors));
                }
            }
            await CommitPrebuiltStructureLinesAsync(
                className,
                algorithm,
                generated,
                cancellationToken);
            return new PrebuiltStructureBatchResult(
                algorithm.AlgorithmId,
                algorithm.DisplayName,
                selectedMaps.Length,
                totalFloors);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public string GetPrebuiltStructureLinePath(MapRecord map, string floorKey)
    {
        var floor = map.Floors.FirstOrDefault(candidate => string.Equals(candidate.Key, floorKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (floor.PrebuiltStructureLine?.IsComplete is not true
            || !string.Equals(
                floor.PrebuiltStructureLine.SourceSha256,
                floor.RecognitionSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"楼层 '{floorKey}' 没有预制线图。");
        var path = GetSafeMapFilePath(
            GetMapDirectory(map.Id),
            floor.PrebuiltStructureLine.FileName);
        if (!File.Exists(path)
            || new FileInfo(path).Length != floor.PrebuiltStructureLine.FileLength)
            throw new InvalidDataException($"楼层 '{floorKey}' 的预制线图缺失或已损坏。");
        return path;
    }

    public string GetPrebuiltStructureAlgorithmPath(MapRecord map, string floorKey)
    {
        var floor = map.Floors.FirstOrDefault(candidate => string.Equals(candidate.Key, floorKey, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"地图不包含楼层 '{floorKey}'。");
        if (floor.PrebuiltStructureLine?.IsComplete is not true)
            throw new InvalidOperationException($"楼层 '{floorKey}' 没有预制线图算法记录。");
        return GetSafeMapFilePath(GetMapDirectory(map.Id), floor.PrebuiltStructureLine.AlgorithmFileName);
    }

    public bool HasCompletePrebuiltStructureLines(MapRecord map) =>
        MapFloorRules.GetOrderedFloors(map).All(floor =>
            floor.PrebuiltStructureLine?.IsComplete is true
            && string.Equals(
                floor.PrebuiltStructureLine.SourceSha256,
                floor.RecognitionSha256,
                StringComparison.OrdinalIgnoreCase)
            && File.Exists(GetPrebuiltStructureLinePath(map, floor.Key))
            && new FileInfo(GetPrebuiltStructureLinePath(map, floor.Key)).Length == floor.PrebuiltStructureLine.FileLength
            && File.Exists(GetPrebuiltStructureAlgorithmPath(map, floor.Key)));

    private async Task CommitPrebuiltStructureLinesAsync(
        string className,
        IdvaStructureAlgorithm algorithm,
        IReadOnlyList<GeneratedPrebuiltLine> generated,
        CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var catalog = await ReadCatalogAsync();
            var latestMaps = catalog.Maps
                .Where(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (latestMaps.Sum(map => MapFloorRules.GetOrderedFloors(map).Count) != generated.Count)
                throw new InvalidOperationException("批处理期间地图类内容发生变化，请重新生成。");
            foreach (var item in generated)
            {
                var map = latestMaps.SingleOrDefault(candidate => candidate.Id == item.MapId)
                    ?? throw new InvalidOperationException("批处理期间地图被移除，请重新生成。");
                var floor = map.Floors.SingleOrDefault(candidate => string.Equals(candidate.Key, item.FloorKey, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException("批处理期间楼层被移除，请重新生成。");
                if (!string.Equals(floor.RecognitionSha256, item.SourceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("批处理期间裁剪后图像发生变化，请重新生成。");
            }
            foreach (var item in generated)
            {
                var map = latestMaps.Single(candidate => candidate.Id == item.MapId);
                var floor = map.Floors.Single(candidate => string.Equals(candidate.Key, item.FloorKey, StringComparison.Ordinal));
                var mapDirectory = GetMapDirectory(map.Id);
                const string algorithmFileName = "prebuilt-structure.idva";
                var lineFileName = $"prebuilt-{floor.Key}.png";
                await ReplaceFileAsync(
                    Path.Combine(mapDirectory, algorithmFileName),
                    algorithm.PackageBytes,
                    cancellationToken);
                await ReplaceFileAsync(
                    item.StagedPath,
                    Path.Combine(mapDirectory, lineFileName),
                    cancellationToken);
                floor.PrebuiltStructureLine = new PrebuiltStructureLineAsset
                {
                    FileName = lineFileName,
                    Sha256 = item.OutputSha256,
                    SourceSha256 = item.SourceSha256,
                    Width = item.Width,
                    Height = item.Height,
                    FileLength = item.FileLength,
                    AlgorithmId = algorithm.AlgorithmId,
                    AlgorithmFileName = algorithmFileName,
                    AlgorithmSha256 = algorithm.Sha256,
                    AlgorithmSchemaVersion = algorithm.SchemaVersion
                };
            }
            await WriteCatalogAsync(catalog);
            foreach (var map in latestMaps)
                RemoveSupersededPrebuiltFiles(GetMapDirectory(map.Id));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task ImportPrebuiltStructureLineAsync(
        string stagingDirectory,
        FloorDefinition floor,
        string floorKey,
        MapDraft draft)
    {
        var asset = floor.PrebuiltStructureLine;
        if (asset?.IsComplete is not true
            || !draft.PrebuiltStructureLinePaths.TryGetValue(floorKey, out var lineSource)
            || string.IsNullOrWhiteSpace(draft.PrebuiltStructureAlgorithmPath)
            || !File.Exists(lineSource)
            || !File.Exists(draft.PrebuiltStructureAlgorithmPath))
        {
            floor.PrebuiltStructureLine = null;
            return;
        }
        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(draft.PrebuiltStructureAlgorithmPath);
        var lineSha = await ComputeFileSha256Async(lineSource, CancellationToken.None);
        if (!string.Equals(algorithm.AlgorithmId, asset.AlgorithmId, StringComparison.Ordinal)
            || !string.Equals(algorithm.Sha256, asset.AlgorithmSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(lineSha, asset.Sha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(floor.RecognitionSha256, asset.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            floor.PrebuiltStructureLine = null;
            return;
        }
        using var line = OpenCvSharp.Cv2.ImRead(lineSource, OpenCvSharp.ImreadModes.Grayscale);
        if (line.Empty()
            || line.Width != floor.RecognitionWidth
            || line.Height != floor.RecognitionHeight)
        {
            floor.PrebuiltStructureLine = null;
            return;
        }
        const string algorithmFileName = "prebuilt-structure.idva";
        var lineFileName = $"prebuilt-{floorKey}.png";
        await File.WriteAllBytesAsync(
            Path.Combine(stagingDirectory, algorithmFileName),
            algorithm.PackageBytes);
        await using (var input = File.OpenRead(lineSource))
        await using (var output = File.Create(Path.Combine(stagingDirectory, lineFileName)))
            await input.CopyToAsync(output);
        asset.FileName = lineFileName;
        asset.AlgorithmFileName = algorithmFileName;
        asset.Width = line.Width;
        asset.Height = line.Height;
        asset.FileLength = new FileInfo(lineSource).Length;
    }

    private static async Task ReplaceFileAsync(
        string destination,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    private static async Task ReplaceFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        await using (var input = File.OpenRead(source))
        await using (var output = File.Create(temporary))
            await input.CopyToAsync(output, cancellationToken);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void RemoveSupersededPrebuiltFiles(string mapDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(mapDirectory, "prebuilt-*"))
        {
            var fileName = Path.GetFileName(path);
            if ((fileName.StartsWith("prebuilt-structure-", StringComparison.OrdinalIgnoreCase)
                    && fileName.EndsWith(".idva", StringComparison.OrdinalIgnoreCase))
                || Regex.IsMatch(fileName, "^prebuilt-.+-[0-9a-f]{12}-[0-9a-f]{12}\\.png$", RegexOptions.IgnoreCase))
                File.Delete(path);
        }
    }

    private sealed record GeneratedPrebuiltLine(
        Guid MapId,
        string FloorKey,
        string SourceSha256,
        string StagedPath,
        string OutputSha256,
        int Width,
        int Height,
        long FileLength);
}
