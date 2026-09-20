namespace IDVBuff.Features.Maps;

public readonly record struct MapGameToggleTransition(bool IsOpen, int Version);

/// <summary>
/// Tracks the expected state of the game's own map while rejecting stale
/// delayed-open work after a newer open/close input.
/// </summary>
public sealed class MapGameToggleState
{
    private int _openPipelineVersion = -1;

    public bool IsOpen { get; private set; }
    public int Version { get; private set; }

    public MapGameToggleTransition Toggle()
    {
        IsOpen = !IsOpen;
        Version++;
        if (!IsOpen)
            _openPipelineVersion = -1;
        return new MapGameToggleTransition(IsOpen, Version);
    }

    public void MarkOpen()
    {
        IsOpen = true;
        Version++;
        // An explicit scan already owns the scan/alignment pipeline for this
        // open map. The game-map binding must only close it next.
        _openPipelineVersion = Version;
    }

    /// <summary>
    /// Synchronizes the runtime state with an externally controlled game map.
    /// This is used by the real CLI after overlay_game has sent the same
    /// XButton1 event that a player would send.  Unlike <see cref="MarkOpen"/>
    /// it leaves the open pipeline available for the explicit align command.
    /// </summary>
    public MapGameToggleTransition SetOpenForExternalController(bool isOpen)
    {
        IsOpen = isOpen;
        Version++;
        _openPipelineVersion = -1;
        return new MapGameToggleTransition(IsOpen, Version);
    }

    public void Reset()
    {
        IsOpen = false;
        Version++;
        _openPipelineVersion = -1;
    }

    /// <summary>
    /// Releases the claimed alignment pipeline while keeping the game's map
    /// logically open. A failed alignment must not poison the current open
    /// transition; the next close/reopen cycle can claim a fresh pipeline.
    /// </summary>
    public void ReleaseOpenPipeline() => _openPipelineVersion = -1;

    public bool IsCurrent(MapGameToggleTransition transition) =>
        transition.Version == Version && transition.IsOpen == IsOpen;

    /// <summary>
    /// Claims the one automatic scan/alignment pipeline allowed for an open
    /// transition. Passive monitoring and stale async continuations cannot
    /// claim it again.
    /// </summary>
    public bool TryBeginOpenPipeline(MapGameToggleTransition transition)
    {
        if (!transition.IsOpen
            || !IsCurrent(transition)
            || _openPipelineVersion == transition.Version)
        {
            return false;
        }

        _openPipelineVersion = transition.Version;
        return true;
    }
}
/*
 * 文件职责：MapGameToggleState。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
