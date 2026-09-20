namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 宿主配置变更（无载荷标记消息）。
/// </summary>
public sealed record ConfigChangedMessage;

/// <summary>
/// 宿主分辨率预设切换。
/// </summary>
public sealed record ResolutionChangedMessage(string? ActivePreset);

/// <summary>
/// 宿主检测到当前进程未以管理员权限启动（无载荷标记消息）。
/// </summary>
public sealed record ElevationRequiredMessage;
