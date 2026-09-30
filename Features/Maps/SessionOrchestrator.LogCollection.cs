using System.Diagnostics;
using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void InitializeLogCollection()
    {
        OutputLog.ConfigureApplication(
            BuildVersionInfo.ProductVersion, BuildVersionInfo.BuildVersion, App.IsSafeMode);
        _logCollector.IsEnabled = _settings!.CollectLogs;
        if (_settings.CollectLogs)
            ResolveMapViewportForCurrentWindow();
    }

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
            ResolveMapViewportForCurrentWindow();
            return;
        }

        OutputLog.Shutdown();
        StartupTimeline.Shutdown();
        await _logCollector.ClearDataAsync();
    }
}
