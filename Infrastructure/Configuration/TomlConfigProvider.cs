// IDVB Remaster Phase 1.1 — TOML Configuration Provider

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using Tommy;

namespace IDVBuff.Infrastructure.Configuration;

/// <summary>
/// 基于 Tommy 的 TOML 配置引擎。读取 default.toml + 分辨率预设覆盖，
/// 通过 IConfigProvider 接口暴露类型化访问。
/// </summary>
public sealed class TomlConfigProvider : IConfigProvider, IDisposable
{
    private readonly string _rootDir;
    private readonly object _lock = new();
    private TomlTable _mergedTable;
    private string _activePreset = "2560x1440"; // 默认预设
    private FileSystemWatcher? _watcher;

    public event EventHandler? ConfigChanged;

    public string ActiveResolutionPreset => _activePreset;

    public TomlConfigProvider(string? rootDir = null)
    {
        _rootDir = rootDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB");
        _mergedTable = new TomlTable();
        Reload();
        StartWatching();
    }

    /// <inheritdoc />
    public T Get<T>(string sectionPath) where T : class, new()
    {
        lock (_lock)
        {
            // Walk the dotted path to find the target table
            var parts = sectionPath.Split('.');
            TomlNode? current = _mergedTable;

            foreach (var part in parts)
            {
                if (current is TomlTable table && table.HasKey(part))
                {
                    current = table[part];
                }
                else
                {
                    // Path not found — return default
                    return new T();
                }
            }

            // At the leaf: if it's a table, try to map to T
            if (current is TomlTable leaf)
            {
                return MapTable<T>(leaf);
            }

            return new T();
        }
    }

    /// <summary>
    /// 切换活跃分辨率预设并触发 ConfigChanged。
    /// </summary>
    public void SetActivePreset(string name)
    {
        lock (_lock)
        {
            if (_activePreset == name) return;
            _activePreset = name;
        }
        Reload();
    }

    /// <summary>
    /// 立即重新加载所有 TOML 并触发 ConfigChanged。
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            _mergedTable = BuildMergedTable();
        }
        OnConfigChanged();
    }

    private TomlTable BuildMergedTable()
    {
        var merged = new TomlTable();

        // 1. 加载全局默认值
        var defaultPath = Path.Combine(_rootDir, "IDVB_config.toml");
        if (!File.Exists(defaultPath))
        {
            // 回退到项目内置默认值（构建输出目录）
            defaultPath = FindBuiltinDefault();
        }
        if (File.Exists(defaultPath))
        {
            using var reader = new StreamReader(defaultPath);
            var table = TOML.Parse(reader);
            MergeTable(merged, table);
        }

        // Survey settings live in an independent file so they can evolve without
        // weakening the semantics of the established map-recognition settings.
        var surveyPath = Path.Combine(_rootDir, "IDVB_survey.toml");
        if (!File.Exists(surveyPath))
            surveyPath = Path.Combine(AppContext.BaseDirectory, "IDVB_survey.toml");
        if (File.Exists(surveyPath))
        {
            using var reader = new StreamReader(surveyPath);
            MergeTable(merged, TOML.Parse(reader));
        }

        // 2. 内置预设作为只读基线，AppData 中的用户文件最后覆盖。
        foreach (var presetPath in ResolvePresetReadDirectories(_activePreset))
        {
            foreach (var file in Directory.GetFiles(presetPath, "*.toml"))
            {
                try
                {
                    using var reader = new StreamReader(file);
                    var table = TOML.Parse(reader);
                    MergeTable(merged, table);
                }
                catch
                {
                    // 忽略损坏的预设文件
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// 解析预设目录路径。先查 AppData，若不存在则回退到构建输出目录。
    /// 预设名可能是 "1920x1080 @ 120 DPI" 格式，目录名只取 "1920x1080"。
    /// </summary>
    public string ResolvePresetDirectory(string presetName)
    {
        var dirName = presetName.Split(' ')[0];
        return Path.Combine(_rootDir, "Presets", dirName);
    }

    private IEnumerable<string> ResolvePresetReadDirectories(string presetName)
    {
        var dirName = presetName.Split(' ')[0];
        var names = new[] { dirName, presetName }
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            foreach (var path in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Presets", name),
                Path.Combine(AppContext.BaseDirectory, "Configuration", "Presets", name)
            })
            {
                if (Directory.Exists(path))
                    yield return path;
            }
        }

        foreach (var name in names)
        {
            var path = Path.Combine(_rootDir, "Presets", name);
            if (Directory.Exists(path))
                yield return path;
        }
    }

    private static void MergeTable(TomlTable target, TomlTable source)
    {
        foreach (var key in source.Keys)
        {
            if (target.HasKey(key) && target[key] is TomlTable targetChild &&
                source[key] is TomlTable sourceChild)
            {
                // 递归合并嵌套表
                MergeTable(targetChild, sourceChild);
            }
            else
            {
                target[key] = source[key];
            }
        }
    }

    private static T MapTable<T>(TomlTable table) where T : class, new()
    {
        var result = new T();
        var type = typeof(T);

        foreach (var prop in type.GetProperties())
        {
            if (!prop.CanWrite) continue;

            // 尝试匹配 TOML key（按属性名，支持 PascalCase → snake_case 转换）
            var tomekey = PascalToSnake(prop.Name);
            if (!table.HasKey(tomekey)) tomekey = prop.Name.ToLowerInvariant();
            if (!table.HasKey(tomekey)) continue;

            var node = table[tomekey];
            try
            {
                object? value = null;
                if (prop.PropertyType == typeof(int) && node.IsInteger)
                    value = (int)node.AsInteger;
                else if (prop.PropertyType == typeof(double))
                    value = node.IsFloat ? node.AsFloat : (node.IsInteger ? (double)node.AsInteger : default(double));
                else if (prop.PropertyType == typeof(bool) && node.IsBoolean)
                    value = node.AsBoolean.Value;
                else if (prop.PropertyType == typeof(string) && node.IsString)
                    value = node.AsString;
                else if (prop.PropertyType == typeof(int[]) && node.IsArray)
                    value = node.AsArray.RawArray
                        .Where(x => x.IsInteger).Select(x => (int)x.AsInteger).ToArray();
                if (value != null)
                    prop.SetValue(result, value);
            }
            catch
            {
                // 类型不匹配 — 跳过该属性，保持默认值
            }
        }

        return result;
    }

    private static string PascalToSnake(string pascal) =>
        string.Concat(pascal.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c) : char.ToLower(c).ToString()));

    private static string FindBuiltinDefault()
    {
        // 检查 AppContext.BaseDirectory 下的配置文件
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "IDVB_config.toml");
        if (File.Exists(candidate)) return candidate;

        // 回退到内置嵌入式配置（Phase 1.1 暂用硬编码默认值）
        return string.Empty;
    }

    private void StartWatching()
    {
        try
        {
            var watchDir = _rootDir;
            if (!Directory.Exists(watchDir)) return;

            _watcher = new FileSystemWatcher(watchDir, "*.toml")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;

            // 防抖：500ms 内的多次变化合并为一次重载
            var debounce = new Timer(_ =>
            {
                try { Reload(); }
                catch { /* 静默处理 */ }
            }, null, Timeout.Infinite, Timeout.Infinite);

            _watcher.Changed += (_, _) => debounce.Change(500, Timeout.Infinite);
            _watcher.Created += (_, _) => debounce.Change(500, Timeout.Infinite);
        }
        catch
        {
            // 文件监视失败不是致命错误
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // 由 debounce timer 处理
    }

    private void OnConfigChanged()
    {
        ConfigChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
