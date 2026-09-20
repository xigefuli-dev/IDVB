using System.Text.Json;
using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginSdk;

namespace IdentityVisionBridge.PluginRuntime;

public sealed class PluginSettingsService : IPluginSettings
{
    private readonly IReadOnlyDictionary<string, IdvpSettingDefinition> _definitions;
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _subscriberLock = new();
    private readonly Dictionary<long, Action<PluginSettingsChanged>> _subscribers = [];
    private readonly Action<Exception>? _reportFault;
    private long _nextSubscriberId;
    private PluginSettingsSnapshot _current;

    private PluginSettingsService(
        string settingsPath,
        IReadOnlyDictionary<string, IdvpSettingDefinition> definitions,
        PluginSettingsSnapshot current,
        Action<Exception>? reportFault)
    {
        _settingsPath = settingsPath;
        _definitions = definitions;
        _current = current;
        _reportFault = reportFault;
    }

    public PluginSettingsSnapshot Current => Volatile.Read(ref _current);

    public static async Task<PluginSettingsService> CreateAsync(
        string dataDirectory,
        IReadOnlyList<IdvpSettingDefinition> definitions,
        CancellationToken cancellationToken = default,
        Action<Exception>? reportFault = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "settings.json");
        var definitionMap = definitions.ToDictionary(setting => setting.Key, StringComparer.Ordinal);
        var values = definitions.ToDictionary(
            setting => setting.Key,
            setting => setting.Default.Clone(),
            StringComparer.Ordinal);

        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var persisted = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(
                stream, cancellationToken: cancellationToken) ?? [];
            foreach (var (key, value) in persisted)
            {
                if (definitionMap.TryGetValue(key, out var definition) && IsValidValue(definition, value))
                {
                    values[key] = value.Clone();
                }
            }
        }

        var service = new PluginSettingsService(
            path, definitionMap, new PluginSettingsSnapshot(values), reportFault);
        if (!File.Exists(path))
        {
            await service.PersistAsync(values, cancellationToken);
        }

        return service;
    }

    public IDisposable Subscribe(Action<PluginSettingsChanged> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subscriberLock)
        {
            var id = ++_nextSubscriberId;
            _subscribers.Add(id, handler);
            return new Subscription(this, id);
        }
    }

    public async Task UpdateAsync(string key, JsonElement value, CancellationToken cancellationToken = default)
    {
        if (!_definitions.TryGetValue(key, out var definition) || !IsValidValue(definition, value))
        {
            throw new ArgumentException($"Invalid value for plugin setting {key}.", nameof(value));
        }

        await _gate.WaitAsync(cancellationToken);
        PluginSettingsSnapshot snapshot;
        try
        {
            var values = Current.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            values[key] = value.Clone();
            await PersistAsync(values, cancellationToken);
            snapshot = new PluginSettingsSnapshot(values);
            Volatile.Write(ref _current, snapshot);
        }
        finally
        {
            _gate.Release();
        }

        Action<PluginSettingsChanged>[] subscribers;
        lock (_subscriberLock)
        {
            subscribers = _subscribers.Values.ToArray();
        }

        var change = new PluginSettingsChanged { Snapshot = snapshot, ChangedKeys = [key] };
        foreach (var subscriber in subscribers)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    subscriber(change);
                }
                catch (Exception exception)
                {
                    _reportFault?.Invoke(exception);
                }
            });
        }
    }

    private static bool IsValidValue(IdvpSettingDefinition definition, JsonElement value)
    {
        return definition.Type switch
        {
            "toggle" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "slider" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
                        number >= definition.Minimum && number <= definition.Maximum,
            "choice" => value.ValueKind == JsonValueKind.String &&
                        definition.Options.Any(option => option.Value == value.GetString()),
            "keyBinding" => value.ValueKind == JsonValueKind.String &&
                            value.GetString() is { Length: > 0 and <= 128 },
            _ => false
        };
    }

    private async Task PersistAsync(
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".settings.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await JsonSerializer.SerializeAsync(stream, values, cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void Unsubscribe(long id)
    {
        lock (_subscriberLock)
        {
            _subscribers.Remove(id);
        }
    }

    private sealed class Subscription(PluginSettingsService owner, long id) : IDisposable
    {
        private PluginSettingsService? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(id);
    }
}
