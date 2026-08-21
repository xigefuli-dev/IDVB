using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public enum MapDisplayCalibrationSource
{
    Exact = 0,
    Migrated = 1,
    Derived = 2
}

public sealed class MapDisplayCalibrationProfile
{
    public int SchemaVersion { get; set; } = 1;
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public NormalizedRectangle? MapViewportRegion { get; set; }
    public NormalizedRectangle? FloorDisplayRegion { get; set; }
    public uint LastObservedDpi { get; set; }
    public MapDisplayCalibrationSource Source { get; set; } =
        MapDisplayCalibrationSource.Exact;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsValid =>
        ClientWidth > 0
        && ClientHeight > 0
        && (MapViewportRegion?.IsValid is true
            || FloorDisplayRegion?.IsValid is true);

    public MapDisplayCalibrationProfile Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        ClientWidth = ClientWidth,
        ClientHeight = ClientHeight,
        MapViewportRegion = MapViewportRegion?.Clone(),
        FloorDisplayRegion = FloorDisplayRegion?.Clone(),
        LastObservedDpi = LastObservedDpi,
        Source = Source,
        UpdatedAt = UpdatedAt
    };
}

public sealed partial class MapRuntimeSettings
{
    public List<MapDisplayCalibrationProfile> DisplayCalibrationProfiles
    {
        get;
        set;
    } = [];

    public MapDisplayCalibrationProfile? GetExactDisplayCalibration(
        int clientWidth,
        int clientHeight) =>
        DisplayCalibrationProfiles
            .Where(profile =>
                profile.IsValid
                && profile.ClientWidth == clientWidth
                && profile.ClientHeight == clientHeight)
            .OrderByDescending(profile => profile.UpdatedAt)
            .FirstOrDefault();

    public NormalizedRectangle? ResolveMapViewportRegion(
        int clientWidth,
        int clientHeight) =>
        GetExactDisplayCalibration(clientWidth, clientHeight)
            ?.MapViewportRegion?.Clone()
        ?? (CalibrationClientWidth == clientWidth
            && CalibrationClientHeight == clientHeight
                ? MapViewportRegion?.Clone()
                : null);

    public NormalizedRectangle? ResolveFloorDisplayRegion(
        int clientWidth,
        int clientHeight) =>
        GetExactDisplayCalibration(clientWidth, clientHeight)
            ?.FloorDisplayRegion?.Clone()
        ?? (FloorCalibrationClientWidth == clientWidth
            && FloorCalibrationClientHeight == clientHeight
                ? FloorDisplayRegion?.Clone()
                : null);

    public void UpsertMapViewportCalibration(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight,
        uint observedDpi)
    {
        var profile = GetOrCreateDisplayCalibration(clientWidth, clientHeight);
        profile.MapViewportRegion = region.Clone();
        profile.LastObservedDpi = observedDpi;
        profile.Source = MapDisplayCalibrationSource.Exact;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        MapViewportRegion = region.Clone();
        CalibrationClientWidth = clientWidth;
        CalibrationClientHeight = clientHeight;
        CalibrationVersion = CurrentCalibrationVersion;
    }

    public void UpsertFloorDisplayCalibration(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight,
        uint observedDpi)
    {
        var profile = GetOrCreateDisplayCalibration(clientWidth, clientHeight);
        profile.FloorDisplayRegion = region.Clone();
        profile.LastObservedDpi = observedDpi;
        profile.Source = MapDisplayCalibrationSource.Exact;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        FloorDisplayRegion = region.Clone();
        FloorCalibrationClientWidth = clientWidth;
        FloorCalibrationClientHeight = clientHeight;
        FloorCalibrationVersion = CurrentCalibrationVersion;
    }

    private MapDisplayCalibrationProfile GetOrCreateDisplayCalibration(
        int clientWidth,
        int clientHeight)
    {
        var profile = GetExactDisplayCalibration(clientWidth, clientHeight);
        if (profile is not null)
            return profile;
        profile = new MapDisplayCalibrationProfile
        {
            ClientWidth = clientWidth,
            ClientHeight = clientHeight,
            Source = MapDisplayCalibrationSource.Derived
        };
        DisplayCalibrationProfiles.Add(profile);
        return profile;
    }

    private MapDisplayCalibrationProfile? GetClosestDisplayCalibration(
        int clientWidth,
        int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
            return null;
        var targetAspect = (double)clientWidth / clientHeight;
        return DisplayCalibrationProfiles
            .Where(profile => profile.IsValid)
            .OrderBy(profile =>
                Math.Abs(((double)profile.ClientWidth / profile.ClientHeight)
                    - targetAspect))
            .ThenBy(profile =>
                Math.Abs(profile.ClientWidth - clientWidth)
                + Math.Abs(profile.ClientHeight - clientHeight))
            .FirstOrDefault();
    }

    private void NormalizeDisplayCalibrationProfiles()
    {
        DisplayCalibrationProfiles ??= [];
        if (MapViewportRegion?.IsValid is true
            && CalibrationClientWidth > 0
            && CalibrationClientHeight > 0)
        {
            var migrated = GetOrCreateDisplayCalibration(
                CalibrationClientWidth,
                CalibrationClientHeight);
            migrated.MapViewportRegion ??= MapViewportRegion.Clone();
            if (migrated.Source != MapDisplayCalibrationSource.Exact)
                migrated.Source = MapDisplayCalibrationSource.Migrated;
        }
        if (FloorDisplayRegion?.IsValid is true
            && FloorCalibrationClientWidth > 0
            && FloorCalibrationClientHeight > 0)
        {
            var migrated = GetOrCreateDisplayCalibration(
                FloorCalibrationClientWidth,
                FloorCalibrationClientHeight);
            migrated.FloorDisplayRegion ??= FloorDisplayRegion.Clone();
            if (migrated.Source != MapDisplayCalibrationSource.Exact)
                migrated.Source = MapDisplayCalibrationSource.Migrated;
        }

        DisplayCalibrationProfiles = DisplayCalibrationProfiles
            .Where(profile => profile?.IsValid is true)
            .GroupBy(profile => (profile.ClientWidth, profile.ClientHeight))
            .Select(group => group
                .OrderByDescending(profile => profile.UpdatedAt)
                .First()
                .Clone())
            .ToList();
    }
}
/*
 * 文件职责：MapDisplayCalibrationProfiles。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
