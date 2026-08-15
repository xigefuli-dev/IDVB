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
}
