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
/*
 * 文件职责：MapLogModels。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
