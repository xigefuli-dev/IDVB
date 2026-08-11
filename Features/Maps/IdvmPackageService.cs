using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public enum IdvmExportScope
{
    CurrentClass,
    AllClasses
}

public sealed record IdvmImportResult(
    Guid PackageId,
    IReadOnlyList<string> CreatedClasses,
    IReadOnlyList<MapRecord> ImportedMaps);

public sealed class IdvmImportPlan : IAsyncDisposable
{
    private bool _disposed;

    internal IdvmImportPlan(
        Guid packageId,
        string stagingDirectory,
        IReadOnlyList<MapImportClassDraft> classes)
    {
        PackageId = packageId;
        StagingDirectory = stagingDirectory;
        Classes = classes;
    }

    public Guid PackageId { get; }
    public int ClassCount => Classes.Count;
    public int MapCount => Classes.Sum(item => item.Maps.Count);
    internal string StagingDirectory { get; }
    internal IReadOnlyList<MapImportClassDraft> Classes { get; }
    internal bool IsDisposed => _disposed;

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;
        _disposed = true;
        try
        {
            if (Directory.Exists(StagingDirectory))
                Directory.Delete(StagingDirectory, recursive: true);
        }
        catch
        {
            // A later temp cleanup may remove files held by an image decoder.
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Reads and writes the portable, untrusted IDVM interchange format.</summary>
public sealed partial class IdvmPackageService
{
    private const int HeaderSize = 80;
    private const int MaxEntries = 4096;
    private const long MaxSingleFileBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const double CoordinateTolerance = 0.000001d;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        MaxDepth = 64,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly MapRepository _repository;

    public IdvmPackageService(MapRepository repository)
    {
        _repository = repository;
    }

    public async Task ExportAsync(
        IdvmExportScope scope,
        string? className,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("导出目标不能为空。", nameof(destination));

        var snapshot = await _repository.GetCatalogSnapshotAsync();
        var selectedMaps = scope == IdvmExportScope.CurrentClass
            ? snapshot.Maps.Where(map => string.Equals(
                map.Class,
                className,
                StringComparison.OrdinalIgnoreCase)).ToArray()
            : snapshot.Maps.ToArray();
        if (selectedMaps.Length == 0)
            throw new InvalidOperationException("所选范围没有可导出的地图。");

        var selectedClasses = snapshot.Classes
            .Where(name => selectedMaps.Any(map => string.Equals(
                map.Class,
                name,
                StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var staging = CreateTemporaryDirectory("export");
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(destinationDirectory);
        var temporaryDestination = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var packageId = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow;
            var classIds = selectedClasses.ToDictionary(
                name => name,
                _ => Guid.NewGuid(),
                StringComparer.OrdinalIgnoreCase);
            var manifest = new ManifestDto
            {
                Format = "idvm",
                FormatVersion = "1.0",
                PackageType = "class-set",
                PackageId = packageId,
                CreatedAt = createdAt,
                MinimumReader = "1.0",
                Capabilities = new CapabilitiesDto()
            };

            foreach (var classLabel in selectedClasses)
            {
                var maps = selectedMaps
                    .Where(map => string.Equals(map.Class, classLabel, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(map => map.SequenceNumber)
                    .ToArray();
                var classId = classIds[classLabel];
                manifest.Classes.Add(new ManifestClassDto
                {
                    ClassId = classId,
                    Name = classLabel,
                    MapIds = maps.Select(map => map.Id).ToList()
                });
                foreach (var map in maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    manifest.Maps.Add(await WriteMapPayloadAsync(
                        staging,
                        classId,
                        map,
                        cancellationToken));
                }
            }

            foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relative = ToLogicalPath(staging, file);
                manifest.Files.Add(new ManifestFileDto
                {
                    Path = relative,
                    Size = new FileInfo(file).Length,
                    Sha256 = await ComputeSha256Async(file, cancellationToken)
                });
            }

            var manifestBytes = SerializeUtf8(manifest);
            var manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            var header = CreateHeader(packageId, createdAt, SHA256.HashData(manifestBytes));
            await File.WriteAllBytesAsync(Path.Combine(staging, "header"), header, cancellationToken);

            await CreateArchiveAsync(staging, temporaryDestination, cancellationToken);
            File.Move(temporaryDestination, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryDestination))
                File.Delete(temporaryDestination);
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<IdvmImportPlan> InspectAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath)
            || (!File.Exists(packagePath) && !Directory.Exists(packagePath)))
        {
            throw new FileNotFoundException("找不到要导入的 IDVM 数据包。", packagePath);
        }

        var staging = CreateTemporaryDirectory("inspect");
        try
        {
            await ExtractPackageAsync(packagePath, staging, cancellationToken);
            var manifest = await ValidatePackageAsync(staging, cancellationToken);
            var classes = await BuildImportDraftsAsync(staging, manifest, cancellationToken);
            return new IdvmImportPlan(manifest.PackageId, staging, classes);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    public async Task<IdvmImportResult> ImportAsync(
        IdvmImportPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsDisposed || !Directory.Exists(plan.StagingDirectory))
            throw new InvalidOperationException("IDVM 导入计划已失效，请重新选择数据包。");
        try
        {
            var result = await _repository.ImportBatchAsync(plan.Classes, cancellationToken);
            return new IdvmImportResult(plan.PackageId, result.CreatedClasses, result.ImportedMaps);
        }
        finally
        {
            await plan.DisposeAsync();
        }
    }

    // ── 内部类型 ──────────────────────────────────────────────────────

    private readonly record struct ParsedHeader(
        Guid PackageId,
        long CreatedAtUnixMilliseconds,
        byte[] ManifestSha256);

    private sealed class ManifestDto
    {
        public string Format { get; set; } = string.Empty;
        public string FormatVersion { get; set; } = string.Empty;
        public string PackageType { get; set; } = string.Empty;
        public Guid PackageId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public string MinimumReader { get; set; } = string.Empty;
        public List<ManifestClassDto> Classes { get; set; } = [];
        public List<ManifestMapDto> Maps { get; set; } = [];
        public List<ManifestFileDto> Files { get; set; } = [];
        public CapabilitiesDto Capabilities { get; set; } = new();
    }

    private sealed class ManifestClassDto
    {
        public Guid ClassId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Guid> MapIds { get; set; } = [];
    }

    private sealed class ManifestMapDto
    {
        public Guid MapId { get; set; }
        public int MapVersion { get; set; }
        public string Name { get; set; } = string.Empty;
        public Guid ClassId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Root { get; set; } = string.Empty;
        public List<ManifestFloorDto> Floors { get; set; } = [];
    }

    private sealed class ManifestFloorDto
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string Image { get; set; } = string.Empty;
    }

    private sealed class ManifestFileDto
    {
        public string Path { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed class CapabilitiesDto
    {
        public bool MultiClass { get; set; } = true;
        public bool MultiFloor { get; set; } = true;
        public bool RecognitionAnchors { get; set; } = true;
        public bool DerivedCache { get; set; }
    }

    private sealed class MetadataDto
    {
        public int SchemaVersion { get; set; } = 1;
        public MetadataMapDto Map { get; set; } = new();
        public List<MetadataFloorDto> Floors { get; set; } = [];
        public RecognitionSettingsDto Recognition { get; set; } = new();
    }

    private sealed class MetadataMapDto
    {
        public Guid Id { get; set; }
        public Guid ClassId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public Guid? SourceProjectId { get; set; }
        public long? SourceProjectRevision { get; set; }
        public string? SourceVisualSha256 { get; set; }
        public string? SourceStructureSha256 { get; set; }
        public string CoordinateSystem { get; set; } = string.Empty;
    }

    private sealed class MetadataFloorDto
    {
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public string Image { get; set; } = string.Empty;
        public string? RecognitionImage { get; set; }
        public int ImageWidth { get; set; }
        public int ImageHeight { get; set; }
        public int OrientationDegrees { get; set; }
        public RectangleDto? RecognitionRegion { get; set; }
        public RectangleDto ValidMapBounds { get; set; } = new() { Width = 1d, Height = 1d };
        /// <summary>侧门特征图元数据（可选，旧版 IDVM 包不含此字段）。</summary>
        public SideEntranceFeatureDto? SideEntranceFeature { get; set; }
    }

    /// <summary>侧门特征图的可移植元数据，写入 metadata.json。</summary>
    private sealed class SideEntranceFeatureDto
    {
        /// <summary>特征图在 IDVM 包内的逻辑路径（{root}/data/floor-n-side-entrance-feature.png）。</summary>
        public string File { get; set; } = string.Empty;
        /// <summary>实际中心点 X（识别图像素，边界挤压后）。</summary>
        public double CenterX { get; set; }
        /// <summary>实际中心点 Y（识别图像素，边界挤压后）。</summary>
        public double CenterY { get; set; }
        /// <summary>实际裁剪半径（像素）。</summary>
        public int Radius { get; set; }
    }

    private sealed class RecognitionSettingsDto
    {
        public int SchemaVersion { get; set; } = 1;
        public WholeImageDto WholeImage { get; set; } = new();
    }

    private sealed class WholeImageDto
    {
        public bool Enabled { get; set; }
        public double Weight { get; set; } = 0.15d;
        public double AnnotatedReferencePenalty { get; set; } = 0.55d;
        public bool ReferenceMayContainAnnotations { get; set; } = true;
    }

    private sealed class GatesDto
    {
        public int SchemaVersion { get; set; } = 1;
        public List<GateDto> Gates { get; set; } = [];
    }

    private sealed class GateDto
    {
        public string Id { get; set; } = string.Empty;
        public string FloorKey { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public RectangleDto Bounds { get; set; } = new();
        public double DirectionDegrees { get; set; }
        public bool Enabled { get; set; } = true;
        public double Confidence { get; set; } = 1d;
    }

    private sealed class AnchorsDto
    {
        public int SchemaVersion { get; set; } = 3;
        public Dictionary<string, AnchorFloorDto> Floors { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class AnchorFloorDto
    {
        public List<AnchorDto> Anchors { get; set; } = [];
        public List<RectangleDto> WholeImageIgnoreRegions { get; set; } = [];
        public List<AnnotationDto> Annotations { get; set; } = [];
    }

    private sealed class AnchorDto
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public double Weight { get; set; }
        public RectangleDto? Bounds { get; set; }
        public bool BuiltIn { get; set; }
        public string? GateId { get; set; }
    }

    private sealed class AnnotationDto
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public int ColorIndex { get; set; }
        public string? Color { get; set; }
        public RectangleDto? Bounds { get; set; }
        public PointDto? Start { get; set; }
        public PointDto? End { get; set; }
        public string? Text { get; set; }
        public string? FontFamily { get; set; }
        public double? FontSize { get; set; }
        public bool? IsBold { get; set; }
        public bool? IsItalic { get; set; }
        public bool? IsStrikethrough { get; set; }
    }

    private sealed class PointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }

    private sealed class RectangleDto
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}
