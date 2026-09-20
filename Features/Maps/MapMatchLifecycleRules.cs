namespace IDVBuff.Features.Maps;

public enum MapAlignmentPrerequisiteKind
{
    DoubleGateInitialScan,
    SideEntranceInitialScan,
    DefaultDualGateAlignment,
    DefaultSingleGateAlignment,
    DefaultStructureAlignment,
    SideSingleGateAlignment,
    SideStructureAlignment,
    OtherFloorStructureAlignment
}

/// <summary>
/// A selected map is valid only inside the match version that selected it.
/// Persisted settings are not sufficient proof that a later match may reuse
/// the map, alignment seed, or any tracking observation from that match.
/// </summary>
public sealed class MapMatchMapLease
{
    public Guid? MapId { get; private set; }
    public int MatchVersion { get; private set; }

    public void Bind(MapMatchSnapshot match, Guid mapId)
    {
        if (!match.IsStarted)
            throw new InvalidOperationException("A map can be selected only for an active match.");
        if (mapId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(mapId));

        MapId = mapId;
        MatchVersion = match.Version;
    }

    public bool IsCurrent(MapMatchSnapshot match, Guid mapId) =>
        match.IsStarted
        && MapId == mapId
        && MatchVersion == match.Version;

    public void Clear()
    {
        MapId = null;
        MatchVersion = 0;
    }
}

public static class MapMatchLifecycleRules
{
    public static MapRuntimeSettings CreateSettingsWithoutMatchSelection(
        MapRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var cleared = settings.Clone();
        cleared.SelectedMapId = null;
        return cleared;
    }

    public static bool CanStart(
        MapAlignmentPrerequisiteKind operation,
        MapMatchSnapshot currentMatch,
        MapMatchSnapshot operationMatch,
        FirstScanStrategy configuredStrategy,
        MapMatchMapLease mapLease,
        Guid? selectedMapId = null,
        MapAlignmentSession? alignmentSession = null,
        MapOverlayTransform? floorScaleSeed = null)
    {
        ArgumentNullException.ThrowIfNull(mapLease);
        if (!currentMatch.IsStarted
            || currentMatch.Version != operationMatch.Version
            || currentMatch.State != operationMatch.State
            || !string.Equals(
                currentMatch.MapClass,
                operationMatch.MapClass,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (operation is MapAlignmentPrerequisiteKind.DoubleGateInitialScan
            or MapAlignmentPrerequisiteKind.SideEntranceInitialScan)
        {
            var expectedStrategy = operation
                == MapAlignmentPrerequisiteKind.DoubleGateInitialScan
                    ? FirstScanStrategy.DoubleGate
                    : FirstScanStrategy.SideEntrance;
            return configuredStrategy == expectedStrategy
                && mapLease.MapId is null
                && selectedMapId is null
                && alignmentSession is null;
        }

        if (selectedMapId is not { } mapId
            || !mapLease.IsCurrent(currentMatch, mapId))
        {
            return false;
        }

        if (operation
            == MapAlignmentPrerequisiteKind.OtherFloorStructureAlignment)
        {
            return IsValidTransform(floorScaleSeed);
        }

        if (operation == MapAlignmentPrerequisiteKind.DefaultDualGateAlignment)
        {
            return configuredStrategy == FirstScanStrategy.DoubleGate;
        }

        if (alignmentSession is null
            || alignmentSession.MapId != mapId
            || !IsValidTransform(alignmentSession.LockedTransform))
        {
            return false;
        }

        var isDefaultStrategy = operation is
            MapAlignmentPrerequisiteKind.DefaultDualGateAlignment
            or MapAlignmentPrerequisiteKind.DefaultSingleGateAlignment
            or MapAlignmentPrerequisiteKind.DefaultStructureAlignment;
        if (isDefaultStrategy)
        {
            return configuredStrategy == FirstScanStrategy.DoubleGate
                && alignmentSession.HasGatePairLock;
        }

        return configuredStrategy == FirstScanStrategy.SideEntrance
            && alignmentSession.SideEntranceScanPriorConfidence > 0d
            && !alignmentSession.HasGatePairLock;
    }

    private static bool IsValidTransform(MapOverlayTransform? transform) =>
        transform is not null
        && double.IsFinite(transform.ScaleX)
        && double.IsFinite(transform.ScaleY)
        && transform.ScaleX > 0.05d
        && transform.ScaleY > 0.05d
        && transform.ReferenceWidth > 0
        && transform.ReferenceHeight > 0;
}
/*
 * 文件职责：MapMatchLifecycleRules。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
