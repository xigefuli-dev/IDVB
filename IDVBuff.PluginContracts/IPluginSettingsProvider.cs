namespace IDVBuff.PluginContracts;

/// <summary>
/// 插件可选实现的设置提供者。宿主 TeachingTip 管理器（TTM）据此
/// 渲染该插件的 SettingPage 并统一托管生命周期；插件同时实现
/// <see cref="IPlugin"/> 与 <c>IPluginSettingsProvider</c> 即可。
/// </summary>
[Obsolete("Legacy built-in compatibility contract. Third-party settings must be declared in manifest.json.")]
public interface IPluginSettingsProvider
{
    /// <summary>设置项描述列表，顺序即设置页中的显示顺序。</summary>
    IReadOnlyList<IPluginSetting> Settings { get; }

    /// <summary>
    /// 读取某键的当前值。宿主约定的返回类型与描述符对应：
    /// toggle→bool、slider→double、choice→选中项字符串、key binding→稳定存储字符串；
    /// 未设置返回 null。
    /// </summary>
    object? GetSettingValue(string key);

    /// <summary>
    /// 写入某键的值。宿主保证传入类型与描述符一致，插件应自行钳制
    /// 到合理范围并安全地应用到运行时；键位设置应解析为
    /// <see cref="PluginInputBinding.StorageValue"/>。
    /// </summary>
    void SetSettingValue(string key, object? value);
}
