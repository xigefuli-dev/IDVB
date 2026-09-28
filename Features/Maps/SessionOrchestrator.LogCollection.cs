using System.Diagnostics;
using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task SetCollectLogsAsync(bool enabled)
    {
        var previous = _settings!.CollectLogs;
        _settings.CollectLogs = enabled;
        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            _settings.CollectLogs = previous;
            throw;
        }

        if (enabled)
        {
            OutputLog.Initialize(
                captureFirstChanceExceptions: Debugger.IsAttached
                    || AppDataPaths.IsTestBuild);
            _logCollector.IsEnabled = true;
            return;
        }

        OutputLog.Shutdown();
        StartupTimeline.Shutdown();
        await _logCollector.ClearDataAsync();
    }
}
