namespace IDVBuff.Features.Maps;

public sealed class PrebuiltStructureLineAsset
{
    public string FileName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileLength { get; set; }
    public string AlgorithmId { get; set; } = string.Empty;
    public string AlgorithmFileName { get; set; } = string.Empty;
    public string AlgorithmSha256 { get; set; } = string.Empty;
    public string AlgorithmSchemaVersion { get; set; } = string.Empty;

    public bool IsComplete => !string.IsNullOrWhiteSpace(FileName)
        && Sha256.Length == 64
        && SourceSha256.Length == 64
        && Width > 0
        && Height > 0
        && FileLength > 0
        && !string.IsNullOrWhiteSpace(AlgorithmId)
        && !string.IsNullOrWhiteSpace(AlgorithmFileName)
        && AlgorithmSha256.Length == 64
        && !string.IsNullOrWhiteSpace(AlgorithmSchemaVersion);

    public PrebuiltStructureLineAsset Clone() => new()
    {
        FileName = FileName,
        Sha256 = Sha256,
        SourceSha256 = SourceSha256,
        Width = Width,
        Height = Height,
        FileLength = FileLength,
        AlgorithmId = AlgorithmId,
        AlgorithmFileName = AlgorithmFileName,
        AlgorithmSha256 = AlgorithmSha256,
        AlgorithmSchemaVersion = AlgorithmSchemaVersion
    };
}
