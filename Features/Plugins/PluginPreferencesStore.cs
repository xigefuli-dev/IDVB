using System.Text;
using System.Text.Json;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// Persists the user's plugin enablement choices and per-plugin setting values
/// independently from the read-only TOML algorithm configuration.
///
/// 数据安全约定：单文件、单一写者（<see cref="_gate"/>）、原子写
/// （.tmp + File.Move）。加载时做 schema 版本校验与非法项修剪，损坏文件
/// 回退默认且不阻止启动；写入只接受 JSON 原语并校验键长度。
/// </summary>
public sealed class PluginPreferencesStore
{
    public const string DefaultFileName = "plugin-preferences.json";

    /// <summary>当前文档 schema 版本；高于此值的文件视为未来格式，仅保留启用状态。</summary>
    public const int CurrentSchemaVersion = 2;

    private const int MaxPluginIdLength = 128;
    private const int MaxKeyLength = 128;
    private const int MaxStringValueLength = 4096;
    private const int MaxPluginCount = 256;
    private const int MaxKeysPerPlugin = 256;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private HashSet<string> _disabledPluginIds;
    private Dictionary<string, Dictionary<string, JsonElement>> _pluginSettings;

    public PluginPreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            DefaultFileName);
        var loaded = LoadDocument();
        _disabledPluginIds = loaded.Disabled;
        _pluginSettings = loaded.Settings;
    }

    public bool IsEnabled(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        lock (_gate)
            return !_disabledPluginIds.Contains(pluginId);
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        lock (_gate)
        {
            var next = new HashSet<string>(
                _disabledPluginIds,
                StringComparer.OrdinalIgnoreCase);
            if (enabled)
                next.Remove(pluginId);
            else
                next.Add(pluginId);

            if (next.SetEquals(_disabledPluginIds))
                return;

            SaveDocument(next, _pluginSettings);
            _disabledPluginIds = next;
        }
    }

    /// <summary>
    /// 读取某个插件的某键设置值。无效输入返回 false（Try 模式不抛异常）。
    /// </summary>
    public bool TryGetSetting(string pluginId, string key, out JsonElement value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(pluginId) || string.IsNullOrWhiteSpace(key))
            return false;

        lock (_gate)
        {
            if (_pluginSettings.TryGetValue(pluginId, out var pluginSettings)
                && pluginSettings.TryGetValue(key, out var element))
            {
                value = element;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 写入某个插件的某键设置值。只接受 JSON 原语（bool/number/string）；
    /// 键经 trim 与长度校验，非法输入抛 <see cref="ArgumentException"/>。
    /// </summary>
    public void SetSetting(string pluginId, string key, JsonElement value)
    {
        var normalizedPluginId = ValidateTokenOrThrow(pluginId, nameof(pluginId), MaxPluginIdLength);
        var normalizedKey = ValidateTokenOrThrow(key, nameof(key), MaxKeyLength);
        if (!IsPrimitive(value))
            throw new ArgumentException(
                "设置值只允许 JSON 原语（bool/number/string）。", nameof(value));
        if (value.ValueKind == JsonValueKind.String
            && value.GetString()?.Length > MaxStringValueLength)
        {
            throw new ArgumentException(
                $"设置值字符串长度不能超过 {MaxStringValueLength}。", nameof(value));
        }

        lock (_gate)
        {
            var nextSettings = CloneSettings(_pluginSettings);
            if (!nextSettings.TryGetValue(normalizedPluginId, out var pluginSettings))
            {
                if (nextSettings.Count >= MaxPluginCount)
                    throw new InvalidOperationException(
                        $"插件设置数量不能超过 {MaxPluginCount}。");
                pluginSettings = new Dictionary<string, JsonElement>(
                    StringComparer.OrdinalIgnoreCase);
                nextSettings[normalizedPluginId] = pluginSettings;
            }

            if (!pluginSettings.ContainsKey(normalizedKey)
                && pluginSettings.Count >= MaxKeysPerPlugin)
            {
                throw new InvalidOperationException(
                    $"单个插件的设置键数量不能超过 {MaxKeysPerPlugin}。");
            }

            pluginSettings[normalizedKey] = value;
            SaveDocument(_disabledPluginIds, nextSettings);
            _pluginSettings = nextSettings;
        }
    }

    /// <summary>
    /// 在插件 OnEnable 前恢复设置，使持久化值不仅在打开设置页时才生效。
    /// 无效或缺失值回退到描述符默认值。
    /// </summary>
    public void RestoreSettings(IPluginSettingsProvider provider, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        foreach (var setting in provider.Settings)
        {
            object? value = setting switch
            {
                PluginToggleSetting toggle => toggle.DefaultValue,
                PluginSliderSetting slider => slider.DefaultValue,
                PluginChoiceSetting choice when choice.Options.Length > 0 => choice.DefaultValue,
                PluginKeyBindingSetting binding =>
                    PluginInputBinding.TryParse(
                        binding.DefaultValue,
                        binding.AllowedKinds,
                        out _)
                            ? binding.DefaultValue
                            : null,
                _ => null
            };
            if (TryGetSetting(pluginId, setting.Key, out var stored)
                && TryRestoreSetting(setting, stored, out var restored))
            {
                value = restored;
            }
            if (value is null)
                continue;
            try
            {
                provider.SetSettingValue(setting.Key, value);
            }
            catch
            {
                // A malformed third-party setting must not prevent host startup.
            }
        }
    }

    private static bool TryRestoreSetting(
        IPluginSetting setting,
        JsonElement stored,
        out object? value)
    {
        switch (setting)
        {
            case PluginToggleSetting
                when stored.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = stored.GetBoolean();
                return true;
            case PluginSliderSetting slider
                when stored.ValueKind == JsonValueKind.Number
                    && stored.TryGetDouble(out var raw)
                    && double.IsFinite(raw):
                value = Math.Clamp(raw, slider.Minimum, slider.Maximum);
                return true;
            case PluginChoiceSetting choice
                when stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } text
                    && choice.Options.Contains(text, StringComparer.Ordinal):
                value = text;
                return true;
            case PluginKeyBindingSetting binding
                when stored.ValueKind == JsonValueKind.String
                    && stored.GetString() is { } bindingText
                    && PluginInputBinding.TryParse(
                        bindingText,
                        binding.AllowedKinds,
                        out _):
                value = bindingText;
                return true;
            default:
                value = null;
                return false;
        }
    }

    private (HashSet<string> Disabled, Dictionary<string, Dictionary<string, JsonElement>> Settings)
        LoadDocument()
    {
        try
        {
            if (!File.Exists(_path))
                return (CreateSet(), CreateSettings());

            var document = JsonSerializer.Deserialize<PluginPreferencesDocument>(
                File.ReadAllText(_path),
                JsonOptions);
            if (document is null)
                return (CreateSet(), CreateSettings());

            var disabled = CreateSet(document.DisabledPluginIds);
            var settings = document.SchemaVersion > CurrentSchemaVersion
                ? CreateSettings() // 未来格式：保留启用状态，设置忽略，下次显式写入时升级回当前版本。
                : SanitizeSettings(document.PluginSettings);
            return (disabled, settings);
        }
        catch
        {
            // A damaged preference file must not prevent the main program
            // from starting. The next explicit change will repair it.
            return (CreateSet(), CreateSettings());
        }
    }

    /// <summary>
    /// 修剪非法插件 id / 键 / 值，并将字典重新 wrap 为大小写不敏感比较器
    /// （JsonSerializer 反序列化 Dictionary 时使用默认 ordinal 比较器）。
    /// </summary>
    private static Dictionary<string, Dictionary<string, JsonElement>> SanitizeSettings(
        Dictionary<string, Dictionary<string, JsonElement>>? source)
    {
        var result = CreateSettings();
        if (source is null)
            return result;

        var pluginCount = 0;
        foreach (var (pluginId, keys) in source)
        {
            if (pluginCount >= MaxPluginCount)
                break;
            var normalizedId = NormalizeToken(pluginId, MaxPluginIdLength);
            if (normalizedId is null || keys is null)
                continue;

            var pluginSettings = new Dictionary<string, JsonElement>(
                StringComparer.OrdinalIgnoreCase);
            var keyCount = 0;
            foreach (var (key, value) in keys)
            {
                if (keyCount >= MaxKeysPerPlugin)
                    break;
                var normalizedKey = NormalizeToken(key, MaxKeyLength);
                if (normalizedKey is null
                    || !IsPrimitive(value)
                    || (value.ValueKind == JsonValueKind.String
                        && value.GetString()?.Length > MaxStringValueLength))
                {
                    continue;
                }
                pluginSettings[normalizedKey] = value;
                keyCount++;
            }
            result[normalizedId] = pluginSettings;
            pluginCount++;
        }
        return result;
    }

    private void SaveDocument(
        HashSet<string> disabledPluginIds,
        Dictionary<string, Dictionary<string, JsonElement>> pluginSettings)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        var document = new PluginPreferencesDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            DisabledPluginIds = disabledPluginIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            PluginSettings = new Dictionary<string, Dictionary<string, JsonElement>>(
                pluginSettings, StringComparer.OrdinalIgnoreCase)
        };

        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(document, JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original persistence exception.
            }

            throw;
        }
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> CloneSettings(
        IReadOnlyDictionary<string, Dictionary<string, JsonElement>> source)
    {
        var clone = CreateSettings();
        foreach (var (pluginId, keys) in source)
        {
            clone[pluginId] = new Dictionary<string, JsonElement>(
                keys, StringComparer.OrdinalIgnoreCase);
        }
        return clone;
    }

    private static Dictionary<string, Dictionary<string, JsonElement>> CreateSettings() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> CreateSet(IEnumerable<string>? ids = null) =>
        new(
            ids?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
            ?? [],
            StringComparer.OrdinalIgnoreCase);

    private static string ValidateTokenOrThrow(string token, string paramName, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token, paramName);
        var trimmed = token.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"长度不能超过 {maxLength}。", paramName);
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("不能包含控制字符。", paramName);
        return trimmed;
    }

    private static string? NormalizeToken(string? token, int maxLength)
    {
        if (token is null)
            return null;
        var trimmed = token.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maxLength || trimmed.Any(char.IsControl))
            return null;
        return trimmed;
    }

    private static bool IsPrimitive(JsonElement element) =>
        element.ValueKind is JsonValueKind.True
            or JsonValueKind.False
            or JsonValueKind.Number
            or JsonValueKind.String;

    private sealed class PluginPreferencesDocument
    {
        /// <summary>旧文件缺省为 0，视为 legacy：只读启用状态。</summary>
        public int SchemaVersion { get; set; }

        public string[] DisabledPluginIds { get; set; } = [];

        public Dictionary<string, Dictionary<string, JsonElement>> PluginSettings { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}
