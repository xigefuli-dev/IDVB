namespace IDVBuff.Features.Maps;

/// <summary>日志级别。</summary>
public enum MapLogLevel
{
    /// <summary>常规信息。</summary>
    Info,
    /// <summary>需要关注的警告。</summary>
    Warning,
    /// <summary>错误或失败。</summary>
    Error
}

/// <summary>日志类别，对应扫描/对齐管线的每一环。</summary>
public enum MapLogCategory
{
    /// <summary>系统级：初始化、关闭、设置变更、权限检查。</summary>
    System,
    /// <summary>扫描生命周期：开始、结束、取消。</summary>
    ScanLifecycle,
    /// <summary>楼层指示器识别。</summary>
    FloorRecognition,
    /// <summary>游戏视口截图。</summary>
    ViewportCapture,
    /// <summary>门模板检测。</summary>
    GateDetection,
    /// <summary>双门几何排名。</summary>
    GeometryRanking,
    /// <summary>地图结构配准。</summary>
    StructureRegistration,
    /// <summary>会话状态转换。</summary>
    Session,
    /// <summary>叠加层渲染。</summary>
    Overlay,
    /// <summary>玩家位置追踪。</summary>
    PlayerTracking,
    /// <summary>Post-alignment ORB frame tracking.</summary>
    OrbTracking,
    /// <summary>插件生命周期与消息。</summary>
    Plugin
}

/// <summary>结构化日志条目。</summary>
public sealed class MapLogEntry
{
    /// <summary>全局自增序号。</summary>
    public int Sequence { get; init; }

    /// <summary>日志产生时间（UTC）。</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>所属阶段类别。</summary>
    public MapLogCategory Category { get; init; }

    /// <summary>日志级别。</summary>
    public MapLogLevel Level { get; init; }

    /// <summary>中文描述信息。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>可选耗时（毫秒），仅 Timing 级别日志有意义。</summary>
    public double? ElapsedMs { get; init; }

    /// <summary>
    /// 结构化键值对，携带该阶段的诊断数据。
    /// 键名沿用 <see cref="MapScanDiagnostics"/> 中的字段名。
    /// </summary>
    public Dictionary<string, object?>? Details { get; init; }
}
