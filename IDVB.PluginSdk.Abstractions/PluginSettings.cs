using System.Text.Json;

namespace IdentityVisionBridge.PluginSdk;

public interface IPluginSettings
{
    PluginSettingsSnapshot Current { get; }

    IDisposable Subscribe(Action<PluginSettingsChanged> handler);
}

public sealed class PluginSettingsSnapshot
{
    private readonly IReadOnlyDictionary<string, JsonElement> _values;

    public PluginSettingsSnapshot(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = new Dictionary<string, JsonElement>(values, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, JsonElement> Values => _values;

    public bool TryGetValue(string key, out JsonElement value) => _values.TryGetValue(key, out value);

    public bool GetBoolean(string key, bool fallback = default) =>
        TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    public double GetNumber(string key, double fallback = default) =>
        TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result)
            ? result
            : fallback;

    public string? GetString(string key, string? fallback = null) =>
        TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : fallback;
}

public sealed record PluginSettingsChanged
{
    public required PluginSettingsSnapshot Snapshot { get; init; }

    public required IReadOnlyList<string> ChangedKeys { get; init; }
}
