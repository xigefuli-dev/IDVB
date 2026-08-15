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
}
