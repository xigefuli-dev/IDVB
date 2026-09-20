namespace IDVBuff.Features.Maps;

internal enum MapFloorSwitchFailure
{
    None,
    NoFloors,
    NoOtherFloor,
    InvalidPosition
}

internal enum MapFloorIdentityState
{
    None,
    Aligned,
    PendingAlignment
}

internal readonly record struct MapFloorIdentityResolution<T>(
    T? Identity,
    MapFloorIdentityState State)
    where T : class;

internal static class MapFloorIdentityRules
{
    public static MapFloorIdentityResolution<T> Resolve<T>(
        T? aligned,
        T? pending)
        where T : class => aligned is not null
            ? new MapFloorIdentityResolution<T>(aligned, MapFloorIdentityState.Aligned)
            : pending is not null
                ? new MapFloorIdentityResolution<T>(
                    pending,
                    MapFloorIdentityState.PendingAlignment)
                : new MapFloorIdentityResolution<T>(null, MapFloorIdentityState.None);
}

internal readonly record struct MapFloorSwitchDecision(
    bool Succeeded,
    string? FromFloorKey,
    string? ToFloorKey,
    MapFloorSwitchFailure Failure)
{
    public static MapFloorSwitchDecision Next(
        MapRecord map,
        string? currentFloorKey)
    {
        var floors = MapFloorRules.GetOrderedFloors(map);
        if (floors.Count == 0)
        {
            return new MapFloorSwitchDecision(
                false,
                currentFloorKey,
                null,
                MapFloorSwitchFailure.NoFloors);
        }

        if (floors.Count == 1)
        {
            return new MapFloorSwitchDecision(
                false,
                currentFloorKey ?? floors[0].Key,
                null,
                MapFloorSwitchFailure.NoOtherFloor);
        }

        var fromFloorKey = string.IsNullOrWhiteSpace(currentFloorKey)
            ? floors[0].Key
            : currentFloorKey;
        return new MapFloorSwitchDecision(
            true,
            fromFloorKey,
            MapFloorRules.GetNextFloorKey(map, fromFloorKey),
            MapFloorSwitchFailure.None);
    }

    public static MapFloorSwitchDecision AtPosition(
        MapRecord map,
        string? currentFloorKey,
        int position)
    {
        var floorKey = MapFloorRules.GetFloorKeyAtPosition(map, position);
        return floorKey is null
            ? new MapFloorSwitchDecision(
                false,
                currentFloorKey,
                null,
                MapFloorSwitchFailure.InvalidPosition)
            : new MapFloorSwitchDecision(
                true,
                currentFloorKey,
                floorKey,
                MapFloorSwitchFailure.None);
    }
}
/*
 * 文件职责：MapFloorSwitchRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
