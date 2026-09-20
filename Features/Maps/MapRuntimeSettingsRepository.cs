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
/*
 * 文件职责：MapRuntimeSettingsRepository。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
