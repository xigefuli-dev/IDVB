// IDVB Remaster Phase 4 — In-memory test configuration provider

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;

namespace IDVBuff.Tests;

/// <summary>
/// 内存中 TOML 配置提供器，用于测试。
/// 不需要文件 I/O，不触发 FileSystemWatcher。
/// </summary>
public sealed class TestConfigProvider : IConfigProvider
{
    private readonly Dictionary<string, object> _sections = new();

    public string ActiveResolutionPreset => "test-preset";

    public event EventHandler? ConfigChanged;

    /// <summary>注册一个配置段。</summary>
    public void RegisterSection<T>(string sectionPath, T config) where T : class, new()
    {
        _sections[sectionPath] = config;
    }

    /// <summary>触发 ConfigChanged 事件（模拟热更新）。</summary>
    public void NotifyChanged()
    {
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public T Get<T>(string sectionPath) where T : class, new()
    {
        if (_sections.TryGetValue(sectionPath, out var obj) && obj is T typed)
            return typed;
        return new T();
    }

    public string ResolvePresetDirectory(string presetName)
        => Path.Combine(Path.GetTempPath(), "IDVB-Tests", "Presets", presetName);

    public void Reload() { /* 内存提供器无文件可重载 */ }
}
