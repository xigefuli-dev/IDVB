using System.Text.Json;
using IDVBuff.Features.Plugins;

namespace IDVBuff.Tests;

public sealed class PluginPreferencesStoreTests
{
    [Fact]
    public void DisabledPluginStateSurvivesASecondStoreInstance()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            var first = new PluginPreferencesStore(path);
            Assert.True(first.IsEnabled("AutoClicker"));

            first.SetEnabled("AutoClicker", enabled: false);

            var second = new PluginPreferencesStore(path);
            Assert.False(second.IsEnabled("AutoClicker"));
            Assert.False(second.IsEnabled("autoclicker"));

            second.SetEnabled("autoclicker", enabled: true);
            var third = new PluginPreferencesStore(path);
            Assert.True(third.IsEnabled("AutoClicker"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidPreferencesFallBackToEnabledDefaults()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{ invalid json");

            var store = new PluginPreferencesStore(path);

            Assert.True(store.IsEnabled("AutoClicker"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SettingValuesSurviveASecondStoreInstance()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new PluginPreferencesStore(path);
            store.SetSetting("auto-clicker", "toggle", JsonSerializer.SerializeToElement(true));
            store.SetSetting("auto-clicker", "delay-ms", JsonSerializer.SerializeToElement(5));
            store.SetSetting("auto-clicker", "mode", JsonSerializer.SerializeToElement("press"));
            store.SetSetting("other", "key", JsonSerializer.SerializeToElement(1.5));

            var reloaded = new PluginPreferencesStore(path);
            Assert.True(GetBool(reloaded, "auto-clicker", "toggle"));
            Assert.Equal(5, GetNumber(reloaded, "auto-clicker", "delay-ms"));
            Assert.Equal("press", GetString(reloaded, "auto-clicker", "mode"));
            Assert.Equal(1.5, GetNumber(reloaded, "other", "key"));
            // 大小写不敏感：读写跨大小写一致。
            Assert.Equal(5, GetNumber(reloaded, "AUTO-CLICKER", "DELAY-MS"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SetSetting_RejectsNonPrimitiveEmptyAndOverlongInput()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new PluginPreferencesStore(path);

            Assert.Throws<ArgumentException>(() =>
                store.SetSetting("p", "k", JsonSerializer.SerializeToElement(new { nested = 1 })));
            Assert.Throws<ArgumentException>(() =>
                store.SetSetting("p", "k", JsonSerializer.SerializeToElement(new[] { 1, 2 })));
            Assert.Throws<ArgumentException>(() =>
                store.SetSetting("p", "k", JsonDocument.Parse("null").RootElement));
            Assert.Throws<ArgumentException>(() =>
                store.SetSetting("p", "", JsonSerializer.SerializeToElement(true)));
            Assert.Throws<ArgumentException>(() =>
                store.SetSetting("", "k", JsonSerializer.SerializeToElement(true)));
            Assert.Throws<ArgumentException>(() =>
                store.SetSetting("p", new string('k', 129), JsonSerializer.SerializeToElement(true)));
            Assert.Throws<ArgumentException>(() =>
                store.SetSetting(new string('p', 129), "k", JsonSerializer.SerializeToElement(true)));

            // 合法的写入应成功且不污染其他键。
            store.SetSetting("p", "k", JsonSerializer.SerializeToElement(1));
            var reloaded = new PluginPreferencesStore(path);
            Assert.Equal(1, GetNumber(reloaded, "p", "k"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Loading_PrunesInvalidEntriesAndKeepsEnabledState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "DisabledPluginIds": ["auto-clicker"],
              "PluginSettings": {
                "auto-clicker": {
                  "valid-int": 5,
                  "valid-bool": true,
                  "invalid-object": { "nested": 1 },
                  "invalid-array": [1, 2],
                  "invalid-null": null,
                  "": "empty-key",
                  "   ": "blank-key"
                },
                "   ": { "k": 1 }
              }
            }
            """);

            var store = new PluginPreferencesStore(path);

            Assert.False(store.IsEnabled("auto-clicker"));
            Assert.Equal(5, GetNumber(store, "auto-clicker", "valid-int"));
            Assert.True(GetBool(store, "auto-clicker", "valid-bool"));
            Assert.False(store.TryGetSetting("auto-clicker", "invalid-object", out _));
            Assert.False(store.TryGetSetting("auto-clicker", "invalid-array", out _));
            Assert.False(store.TryGetSetting("auto-clicker", "invalid-null", out _));
            Assert.False(store.TryGetSetting("auto-clicker", "", out _));
            Assert.False(store.TryGetSetting("auto-clicker", "   ", out _));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Loading_FutureSchemaVersionKeepsEnabledStateButIgnoresSettings()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
            {
              "SchemaVersion": 99,
              "DisabledPluginIds": ["auto-clicker"],
              "PluginSettings": { "auto-clicker": { "delay-ms": 5 } }
            }
            """);

            var store = new PluginPreferencesStore(path);

            Assert.False(store.IsEnabled("auto-clicker"));
            Assert.False(store.TryGetSetting("auto-clicker", "delay-ms", out _));

            // 下次显式写入升级回当前 schema，且保留启用状态。
            store.SetSetting("auto-clicker", "delay-ms", JsonSerializer.SerializeToElement(7));
            var reloaded = new PluginPreferencesStore(path);
            Assert.False(reloaded.IsEnabled("auto-clicker"));
            Assert.Equal(7, GetNumber(reloaded, "auto-clicker", "delay-ms"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Loading_LegacyDocumentWithoutSchemaVersionStillLoadsEnabledState()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "IDVB-PluginPreferencesTests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "plugin-preferences.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """{ "DisabledPluginIds": ["auto-clicker"] }""");

            var store = new PluginPreferencesStore(path);
            Assert.False(store.IsEnabled("auto-clicker"));

            // 保存时升级到当前 schema。
            store.SetSetting("auto-clicker", "delay-ms", JsonSerializer.SerializeToElement(5));
            var text = File.ReadAllText(path);
            var document = JsonSerializer.Deserialize<PluginPreferencesDocument>(text);
            Assert.Equal(PluginPreferencesStore.CurrentSchemaVersion, document!.SchemaVersion);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static bool GetBool(PluginPreferencesStore store, string pluginId, string key) =>
        store.TryGetSetting(pluginId, key, out var element) && element.ValueKind == JsonValueKind.True;

    private static double GetNumber(PluginPreferencesStore store, string pluginId, string key) =>
        store.TryGetSetting(pluginId, key, out var element) && element.ValueKind == JsonValueKind.Number
            ? element.GetDouble()
            : throw new Xunit.Sdk.XunitException($"缺少数值设置 {pluginId}/{key}");

    private static string GetString(PluginPreferencesStore store, string pluginId, string key) =>
        store.TryGetSetting(pluginId, key, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw new Xunit.Sdk.XunitException($"缺少字符串设置 {pluginId}/{key}");

    private sealed class PluginPreferencesDocument
    {
        public int SchemaVersion { get; set; }

        public string[] DisabledPluginIds { get; set; } = [];
    }
}
