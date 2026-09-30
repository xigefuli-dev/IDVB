using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.Lifecycle;

namespace IDVBuff;

public partial class App
{
    private static void InitializeOutputLog(bool isCliLaunch)
    {
        OutputLog.ConfigureApplication(
            BuildVersionInfo.ProductVersion, BuildVersionInfo.BuildVersion,
            safeMode: !isCliLaunch && MainProgramPreferences.Load().SafeMode);
        if (MapRuntimeSettingsRepository.IsLogCollectionEnabled())
            OutputLog.Initialize(
                captureFirstChanceExceptions: !isCliLaunch
                    && (System.Diagnostics.Debugger.IsAttached || AppDataPaths.IsTestBuild));
    }
}
