using System.Text.Json;
using IdentityVisionBridge.PluginPackaging;
using IdentityVisionBridge.PluginRuntime;

namespace IDVB.PluginSystem.Tests;

public sealed class PluginSettingsServiceTests
{
    [Fact]
    public async Task DefaultsPersistAndChangesAreRestored()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-plugin-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var definitions = new[]
            {
                new IdvpSettingDefinition
                {
                    Key = "enabled",
                    Type = "toggle",
                    DisplayName = "Enabled",
                    Default = JsonSerializer.SerializeToElement(true)
                }
            };
            var first = await PluginSettingsService.CreateAsync(root, definitions);
            Assert.True(first.Current.GetBoolean("enabled"));
            await first.UpdateAsync("enabled", JsonSerializer.SerializeToElement(false));

            var second = await PluginSettingsService.CreateAsync(root, definitions);
            Assert.False(second.Current.GetBoolean("enabled", true));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidSettingValueIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "idvb-plugin-settings-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var definitions = new[]
            {
                new IdvpSettingDefinition
                {
                    Key = "level",
                    Type = "slider",
                    DisplayName = "Level",
                    Default = JsonSerializer.SerializeToElement(5d),
                    Minimum = 0,
                    Maximum = 10,
                    Step = 1
                }
            };
            var settings = await PluginSettingsService.CreateAsync(root, definitions);
            await Assert.ThrowsAsync<ArgumentException>(() => settings.UpdateAsync(
                "level",
                JsonSerializer.SerializeToElement(20d)));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
