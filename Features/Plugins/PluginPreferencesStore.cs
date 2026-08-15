using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// Persists the user's plugin enablement choices independently from the
/// read-only TOML algorithm configuration.
/// </summary>
public sealed class PluginPreferencesStore
{
    public const string DefaultFileName = "plugin-preferences.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private HashSet<string> _disabledPluginIds;

    public PluginPreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            DefaultFileName);
        _disabledPluginIds = LoadDisabledPluginIds();
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

            SaveDisabledPluginIds(next);
            _disabledPluginIds = next;
        }
    }

    private HashSet<string> LoadDisabledPluginIds()
    {
        try
        {
            if (!File.Exists(_path))
                return CreateSet();

            var document = JsonSerializer.Deserialize<PluginPreferencesDocument>(
                File.ReadAllText(_path),
                JsonOptions);
            return CreateSet(document?.DisabledPluginIds);
        }
        catch
        {
            // A damaged preference file must not prevent the main program
            // from starting. The next explicit change will repair it.
            return CreateSet();
        }
    }

    private void SaveDisabledPluginIds(HashSet<string> disabledPluginIds)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _path + ".tmp";
        var document = new PluginPreferencesDocument
        {
            DisabledPluginIds = disabledPluginIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
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

    private static HashSet<string> CreateSet(IEnumerable<string>? ids = null) =>
        new(
            ids?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
            ?? [],
            StringComparer.OrdinalIgnoreCase);

    private sealed class PluginPreferencesDocument
    {
        public string[] DisabledPluginIds { get; set; } = [];
    }
}
