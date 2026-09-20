namespace IDVBuff.Features.Maps;

public sealed partial class IdvmPackageService
{
    private sealed class PrebuiltStructureLineDto
    {
        public string File { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string SourceSha256 { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public long FileLength { get; set; }
        public string AlgorithmId { get; set; } = string.Empty;
        public string AlgorithmFile { get; set; } = string.Empty;
        public string AlgorithmSha256 { get; set; } = string.Empty;
        public string AlgorithmSchemaVersion { get; set; } = string.Empty;
    }

    private sealed record ImportedPrebuiltStructure(
        PrebuiltStructureLineAsset Asset,
        string LinePath,
        string AlgorithmPath);

    private static async Task<ImportedPrebuiltStructure?> ReadImportedPrebuiltStructureAsync(
        string root,
        ManifestMapDto map,
        MetadataFloorDto floor,
        CancellationToken cancellationToken)
    {
        if (floor.PrebuiltStructureLine is not { } prebuilt)
            return null;
        var linePath = ToPhysicalPath(root, prebuilt.File);
        var algorithmPath = ToPhysicalPath(root, prebuilt.AlgorithmFile);
        var lineHash = await ComputeSha256Async(linePath, cancellationToken);
        var algorithm = await new IdvaStructureLineEngine().LoadAsync(algorithmPath, cancellationToken);
        if (!string.Equals(lineHash, prebuilt.Sha256, StringComparison.Ordinal)
            || new FileInfo(linePath).Length != prebuilt.FileLength
            || !string.Equals(algorithm.AlgorithmId, prebuilt.AlgorithmId, StringComparison.Ordinal)
            || !string.Equals(algorithm.Sha256, prebuilt.AlgorithmSha256, StringComparison.Ordinal)
            || algorithm.SchemaVersion != prebuilt.AlgorithmSchemaVersion)
            throw new InvalidDataException($"地图“{map.Name}”楼层 {floor.Key} 的预制线图登记与文件不一致。");
        return new ImportedPrebuiltStructure(
            new PrebuiltStructureLineAsset
            {
                FileName = Path.GetFileName(prebuilt.File),
                Sha256 = prebuilt.Sha256,
                SourceSha256 = prebuilt.SourceSha256,
                Width = prebuilt.Width,
                Height = prebuilt.Height,
                FileLength = prebuilt.FileLength,
                AlgorithmId = prebuilt.AlgorithmId,
                AlgorithmFileName = Path.GetFileName(prebuilt.AlgorithmFile),
                AlgorithmSha256 = prebuilt.AlgorithmSha256,
                AlgorithmSchemaVersion = prebuilt.AlgorithmSchemaVersion
            },
            linePath,
            algorithmPath);
    }

    private static void ValidatePrebuiltStructureLine(
        ManifestMapDto map,
        MetadataFloorDto floor,
        PrebuiltStructureLineDto prebuilt)
    {
        ValidateLogicalPath(prebuilt.File);
        ValidateLogicalPath(prebuilt.AlgorithmFile);
        if (!prebuilt.File.StartsWith($"{map.Root}/data/", StringComparison.Ordinal)
            || !prebuilt.File.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || !prebuilt.AlgorithmFile.StartsWith($"{map.Root}/data/", StringComparison.Ordinal)
            || !prebuilt.AlgorithmFile.EndsWith(".idva", StringComparison.OrdinalIgnoreCase)
            || !IsSha256(prebuilt.Sha256)
            || !IsSha256(prebuilt.SourceSha256)
            || !IsSha256(prebuilt.AlgorithmSha256)
            || prebuilt.Width <= 0
            || prebuilt.Height <= 0
            || prebuilt.FileLength <= 0
            || string.IsNullOrWhiteSpace(prebuilt.AlgorithmId)
            || prebuilt.AlgorithmSchemaVersion != "1.1")
            throw new InvalidDataException($"Floor {floor.Key} 的预制线图登记无效。");
    }
}
