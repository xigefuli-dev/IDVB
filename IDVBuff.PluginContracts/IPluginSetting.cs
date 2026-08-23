namespace IDVBuff.PluginContracts;

/// <summary>
/// 插件设置项的框架无关描述。宿主 TeachingTip 管理器据此渲染
/// 设置页（SettingPage）中的开关 / 滑条 / 下拉控件。
/// </summary>
public interface IPluginSetting
{
    /// <summary>稳定键，用于读取、写入与持久化。</summary>
    string Key { get; }

    /// <summary>设置页中显示的名称。</summary>
    string DisplayName { get; }

    /// <summary>可选的说明文字（显示在控件下方）。</summary>
    string? Description { get; }
}

/// <summary>开关设置项。</summary>
public sealed class PluginToggleSetting : IPluginSetting
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public bool DefaultValue { get; init; }
}

/// <summary>滑条设置项。</summary>
public sealed class PluginSliderSetting : IPluginSetting
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public double Minimum { get; init; }

    public double Maximum { get; init; }

    public double StepFrequency { get; init; } = 1;

    public double DefaultValue { get; init; }
}

/// <summary>下拉（单选）设置项。</summary>
public sealed class PluginChoiceSetting : IPluginSetting
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    public required string[] Options { get; init; }

    public int DefaultIndex { get; init; }

    /// <summary>默认选中项；索引越界时回退到第一项。</summary>
    public string DefaultValue =>
        Options.Length == 0
            ? throw new InvalidOperationException("PluginChoiceSetting 的 Options 不能为空。")
            : DefaultIndex >= 0 && DefaultIndex < Options.Length
                ? Options[DefaultIndex]
                : Options[0];
}

/// <summary>可在插件设置页录制的键盘 / 鼠标绑定。</summary>
public sealed class PluginKeyBindingSetting : IPluginSetting
{
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>默认绑定的 PluginInputBinding.StorageValue。</summary>
    public required string DefaultValue { get; init; }

    /// <summary>允许录制的设备类型，默认同时允许键盘和鼠标。</summary>
    public PluginInputBindingKinds AllowedKinds { get; init; } =
        PluginInputBindingKinds.All;
}
