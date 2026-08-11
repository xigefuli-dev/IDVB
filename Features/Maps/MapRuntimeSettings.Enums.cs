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
