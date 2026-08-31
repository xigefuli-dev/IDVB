namespace IDVBuff.Features.Maps;

internal sealed class MapCatalogDocument
{
    /// <summary>Local catalog storage schema. Version 16 adds map variant groups.</summary>
    public int StorageSchemaVersion { get; set; }
    public int NextSequenceNumber { get; set; } = 1;
    /// <summary>
    /// Persisted independently of maps so an empty class remains available in the
    /// management UI. Display names are canonicalized by <see cref="MapRepository"/>.
    /// </summary>
    public List<string> Classes { get; set; } = ["S1"];
    public Dictionary<string, MapClassProperties> ClassProperties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MapRecord> Maps { get; set; } = [];
    public List<MapVariantGroup> VariantGroups { get; set; } = [];
}

public sealed class MapVariantGroup
{
    public const int PaletteSize = 12;

    public Guid Id { get; set; }
    public string Class { get; set; } = string.Empty;
    public int PaletteSlot { get; set; }
    public List<Guid> MapIds { get; set; } = [];

    public MapVariantGroup Clone() => new()
    {
        Id = Id,
        Class = Class,
        PaletteSlot = PaletteSlot,
        MapIds = MapIds.ToList()
    };
}

public enum MapVariantGroupChangeKind
{
    Bound,
    Unbound
}

public sealed record MapVariantGroupChangeResult(
    MapVariantGroupChangeKind Kind,
    MapVariantGroup Group);

public sealed class MapClassProperties
{
    public bool RemoveBackground { get; set; }
    /// <summary>
    /// Local-only RGB tolerance used for automatic background removal. This is
    /// intentionally not part of the portable IDVM package contract.
    /// </summary>
    public int BackgroundRemovalIntensity { get; set; } = MapBackgroundProcessor.DefaultBackgroundRemovalIntensity;
    /// <summary>
    /// Case-insensitive floor identity used for Class-wide map scanning.
    /// Null keeps the compatibility behavior where every map uses its own
    /// default primary floor.
    /// </summary>
    public string? ScanFloorKey { get; set; }

    public MapClassProperties Clone() => new()
    {
        RemoveBackground = RemoveBackground,
        BackgroundRemovalIntensity = MapBackgroundProcessor.ClampBackgroundRemovalIntensity(
            BackgroundRemovalIntensity),
        ScanFloorKey = MapScanFloorRules.NormalizeFloorIdentity(ScanFloorKey)
    };

    public override bool Equals(object? obj) => obj is MapClassProperties other
        && other.RemoveBackground == RemoveBackground
        && MapBackgroundProcessor.ClampBackgroundRemovalIntensity(other.BackgroundRemovalIntensity)
            == MapBackgroundProcessor.ClampBackgroundRemovalIntensity(BackgroundRemovalIntensity)
        && string.Equals(
            MapScanFloorRules.NormalizeFloorIdentity(other.ScanFloorKey),
            MapScanFloorRules.NormalizeFloorIdentity(ScanFloorKey),
            StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(
        RemoveBackground,
        MapBackgroundProcessor.ClampBackgroundRemovalIntensity(BackgroundRemovalIntensity),
        MapScanFloorRules.NormalizeFloorIdentity(ScanFloorKey));
}

public sealed record MapCatalogSnapshot(
    IReadOnlyList<string> Classes,
    IReadOnlyList<MapRecord> Maps)
{
    public IReadOnlyDictionary<string, MapClassProperties> ClassProperties { get; init; } =
        new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<MapVariantGroup> VariantGroups { get; init; } = [];
}

public sealed record MapClassDeletionResult(
    string ClassName,
    int DeletedMapCount);
/*
 * 文件职责：MapModels.Catalog。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
