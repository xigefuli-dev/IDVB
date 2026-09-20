using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Velopack.Locators;

namespace IDVBuff.Lifecycle;

/// <summary>
/// Starts the independent updater at most once per day. The updater remains
/// hidden when the installed version is current or the update service is
/// temporarily unavailable, so application startup never depends on network.
/// </summary>
internal static class AutomaticUpdateLauncher
{
    private const string StateFileName = "update-check.json";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    public static void TryLaunch()
    {
        try
        {
            if (!SupportsApplicationUpdates())
                return;

            var updaterPath = Path.Combine(AppContext.BaseDirectory, "Updater", "IDVB.Updater.exe");
            if (!File.Exists(updaterPath))
                return;

            var channel = UpdateChannelPolicy.Resolve();
            var statePath = Path.Combine(AppDataPaths.RootDirectory, StateFileName);
            if (File.Exists(statePath))
            {
                var state = JsonSerializer.Deserialize<UpdateCheckState>(File.ReadAllText(statePath));
                if (state is not null
                    && string.Equals(state.Channel, channel, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - state.LastAttemptUtc < CheckInterval)
                    return;
            }

            Directory.CreateDirectory(AppDataPaths.RootDirectory);
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(new UpdateCheckState(DateTimeOffset.UtcNow, channel)));

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(updaterPath)
            };
            startInfo.ArgumentList.Add("--background");
            startInfo.ArgumentList.Add("--channel");
            startInfo.ArgumentList.Add(channel);
            startInfo.ArgumentList.Add("--from-main-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _ = Process.Start(startInfo);
        }
        catch (Exception exception)
        {
            Diagnostics.OutputLog.Write(
                "WARN",
                "UPDATE/AUTO",
                "Unable to start the background update check.",
                exception);
        }
    }

    private static bool SupportsApplicationUpdates()
    {
        if (VelopackLocator.Current.CurrentlyInstalledVersion is not null)
            return true;

        // The former Inno installation is intentionally allowed to open the
        // migration updater. A loose build directory is neither an installed
        // version nor a migration source and must never be labelled "旧版安装".
        return Directory.EnumerateFiles(
            AppContext.BaseDirectory,
            "unins*.exe",
            SearchOption.TopDirectoryOnly).Any();
    }

    private sealed record UpdateCheckState(DateTimeOffset LastAttemptUtc, string? Channel);
}
