namespace IDVBuff.Features.Maps;

public enum MapInputBindingKind
{
    None,
    Keyboard,
    Mouse
}

[Flags]
public enum MapInputModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>首次扫描策略：双门对齐（默认）或带门前置的侧门扫描。</summary>
public enum FirstScanStrategy
{
    /// <summary>打开游戏原生地图后扫描，用双门向量几何对齐。</summary>
    DoubleGate = 0,
    /// <summary>要求暴露门特征，并使用侧门专用区域匹配地图，适合从侧门进入的场景。</summary>
    SideEntrance = 1
}

public enum MapMouseButton
{
    Left,
    Right,
    Middle,
    XButton1,
    XButton2
}

public enum MapRuntimeBindingTarget
{
    QuickScan,
    OverlayToggle,
    ManualRecognition,
    GameMapToggle,
    ControlPanelToggle,
    SwitchFloor,
    SaveMapCache
}
/*
 * 文件职责：MapRuntimeSettings.Enums。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
