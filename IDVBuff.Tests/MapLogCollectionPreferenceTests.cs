using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapLogCollectionPreferenceTests
{
    [Theory]
    [InlineData("{\"CollectLogs\":true}", true)]
    [InlineData("{\"collectlogs\":false}", false)]
    [InlineData("{}", false)]
    [InlineData("not-json", false)]
    public async Task StartupLoggingFailsClosed(string json, bool expected)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "IDVBuff-LogPreferenceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), json);

            Assert.Equal(
                expected,
                MapRuntimeSettingsRepository.IsLogCollectionEnabled(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
