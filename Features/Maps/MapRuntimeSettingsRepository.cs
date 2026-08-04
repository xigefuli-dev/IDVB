using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed class MapRuntimeSettingsRepository
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _directory;

    public MapRuntimeSettingsRepository(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "MapRuntime");
    }

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public async Task<MapRuntimeSettings> LoadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            if (!File.Exists(SettingsPath))
                return MapRuntimeSettings.CreateDefault();
            var json = await File.ReadAllTextAsync(SettingsPath);
            var hasDeclaredSchema = false;
            try
            {
                using var document = JsonDocument.Parse(json);
                hasDeclaredSchema = document.RootElement.ValueKind
                        == JsonValueKind.Object
                    && document.RootElement.EnumerateObject().Any(
                        property => string.Equals(
                            property.Name,
                            nameof(MapRuntimeSettings.SchemaVersion),
                            StringComparison.OrdinalIgnoreCase));
            }
            catch (JsonException)
            {
                // Deserialization below reports the original malformed file.
            }
            var settings =
                JsonSerializer.Deserialize<MapRuntimeSettings>(
                    json,
                    SerializerOptions)
                ?? new MapRuntimeSettings();
            if (!hasDeclaredSchema)
                settings.SchemaVersion = 0;
            var requiresMigration =
                !hasDeclaredSchema
                || settings.SchemaVersion
                    < MapRuntimeSettings.CurrentSchemaVersion
                || settings.OverlayAlignmentMode
                    != MapOverlayAlignmentMode.Uniform;
            settings.Normalize();
            if (requiresMigration)
            {
                var temporaryPath = $"{SettingsPath}.migration.tmp";
                await using (var migrated = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(
                        migrated,
                        settings,
                        SerializerOptions);
                }
                File.Move(temporaryPath, SettingsPath, overwrite: true);
            }
            return settings;
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task SaveAsync(MapRuntimeSettings settings)
    {
        settings.Normalize();
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            var temporaryPath = $"{SettingsPath}.tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
