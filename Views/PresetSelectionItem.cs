namespace IDVBuff.Views;

/// <summary>
/// 「使用配置文件」下拉框的展示项。「自动」与各分辨率预设统一包装，
/// <see cref="ProfileName"/> 为 null 表示自动匹配。
/// </summary>
internal sealed class PresetSelectionItem
{
    public static PresetSelectionItem Auto { get; } = new("自动", null);

    public string Name { get; }

    /// <summary>对应的预设名；null 表示「自动」。</summary>
    public string? ProfileName { get; }

    public PresetSelectionItem(string name, string? profileName)
    {
        Name = name;
        ProfileName = profileName;
    }
}
