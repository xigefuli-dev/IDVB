using System.Diagnostics;
using System.Text.Json;
using Velopack.Locators;

namespace IDVBuff.Lifecycle;

internal static class UpdateLifecycleState
{
    private const string MarkerFileName = "velopack-install.json";

    public static bool WasRestartedAfterUpdate { get; set; }

    public static void RecordCurrentVelopackInstall()
    {
        try
        {
            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion is null || string.IsNullOrWhiteSpace(locator.RootAppDir))
                return;
            var launcher = Path.Combine(locator.RootAppDir, "IDVB.exe");
            if (!File.Exists(launcher))
                return;
            Directory.CreateDirectory(AppDataPaths.RootDirectory);
            File.WriteAllText(
                Path.Combine(AppDataPaths.RootDirectory, MarkerFileName),
                JsonSerializer.Serialize(new InstallMarker(launcher, DateTimeOffset.UtcNow)));
        }
        catch
        {
            // Failure to write an optional migration marker must not prevent launch.
        }
    }

    public static bool TryRedirectLegacyLaunch(string[] args)
    {
        try
        {
            if (args.Any(argument => string.Equals(
                    argument,
                    "--isolated-dev-instance",
                    StringComparison.OrdinalIgnoreCase)))
                return false;
            if (VelopackLocator.Current.CurrentlyInstalledVersion is not null)
                return false;
            var markerPath = Path.Combine(AppDataPaths.RootDirectory, MarkerFileName);
            if (!File.Exists(markerPath))
                return false;
            var marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(markerPath));
            if (marker is null || !VelopackInstallLayout.IsValidLauncherPath(marker.LauncherPath))
                return false;

            var launcherPath = Path.GetFullPath(marker.LauncherPath);
            if (string.Equals(launcherPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(launcherPath)!
            };
            foreach (var argument in args)
                startInfo.ArgumentList.Add(argument);
            _ = Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record InstallMarker(string LauncherPath, DateTimeOffset UpdatedUtc);
}
