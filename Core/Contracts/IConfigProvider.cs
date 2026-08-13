// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

/// <summary>
/// TOML 配置文件读取抽象。提供类型安全的分段读取能力和配置变更通知。
/// </summary>
public interface IConfigProvider
{
    /// <summary>
    /// 从指定 TOML 分段路径读取配置并反序列化为目标类型。
    /// </summary>
    /// <typeparam name="T">配置段的 POCO 类型，必须有无参构造函数。</typeparam>
    /// <param name="sectionPath">TOML 分段路径，如 "recognition.tuning"。若路径不存在则返回 new T()。</param>
    T Get<T>(string sectionPath) where T : class, new();

    /// <summary>
    /// 配置文件被外部修改或内部重新加载后触发。
    /// </summary>
    event EventHandler? ConfigChanged;

    /// <summary>
    /// 当前活跃的分辨率预设名称（如 "1080p"）。
    /// </summary>
    string ActiveResolutionPreset { get; }

    /// <summary>
    /// 解析预设目录的磁盘路径（优先 AppData，回退到构建输出目录）。
    /// </summary>
    string ResolvePresetDirectory(string presetName);

    /// <summary>
    /// 立即重新加载全部 TOML 配置并合并，触发 <see cref="ConfigChanged"/>。
    /// 供写回配置（如校准区域）后刷新内存合并表，避免读到旧值。
    /// </summary>
    void Reload();
}
