namespace IDVBuff.PluginContracts;

/// <summary>
/// 插件元数据（可选；注册冲突检测仍以 <see cref="IPlugin.Id"/> 为准）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PluginAttribute : Attribute
{
    public PluginAttribute(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
    }

    public string Id { get; }

    public string? DisplayName { get; set; }

    public string? Description { get; set; }

    public string? Version { get; set; }

    public string? Author { get; set; }

    public bool EnabledByDefault { get; set; } = true;

    /// <summary>
    /// 是否常驻/不受对局门控限制。常驻插件在宿主启动时若已启用则立即激活，
    /// 且在局外依然保持运行；切换启用状态时立即触发生命周期回调。
    /// </summary>
    public bool AlwaysActive { get; set; }
}
