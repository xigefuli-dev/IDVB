using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace IDVBuff.Features.GameLaunch;

/// <summary>
/// Starts Identity V through FeverGames' own command bridge. The bridge supplies the
/// platform login session that a bare dwrg.exe process does not receive.
/// </summary>
internal static class FeverGamesGameLauncher
{
    private const string ProtocolCommandPath = @"fevergames\shell\open\command";
    private const string GameRegistryPath = @"Software\FeverGames\FeverGamesInstaller\game\73";
    // `state=startgame` only navigates to the game's page. Fever's protocol parser
    // treats `autoRun=1` as an explicit request to start the selected game.
    private const string GameLaunchUri = FeverGamesLaunchPlan.AutoStartGameUri;

    public static bool TryLaunch(out string failureReason)
    {
        // A live Fever process owns the authenticated launcher session. In that state,
        // launching the registered game command is the same path Fever uses after its
        // own Start Game action, without opening the platform UI again.
        if (IsFeverRunning())
            return TryLaunchThroughActiveFever(out failureReason);

        using var protocolKey = Registry.ClassesRoot.OpenSubKey(ProtocolCommandPath, writable: false);
        if (!FeverGamesLaunchPlan.TryCreate(
                protocolKey?.GetValue(null) as string,
                File.Exists,
                out var plan,
                out failureReason))
        {
            return false;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = plan.LauncherPath,
                Arguments = GameLaunchUri,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Windows 未返回启动器进程。");
            _ = HideFeverGamesWindowsAsync();
            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"无法启动网易发烧游戏：{exception.Message}";
            return false;
        }
    }

    private static bool TryLaunchThroughActiveFever(out string failureReason)
    {
        using var gameKey = Registry.CurrentUser.OpenSubKey(GameRegistryPath, writable: false);
        if (!FeverGamesGameStartPlan.TryCreate(
                gameKey?.GetValue("InstallPath") as string,
                gameKey?.GetValue("StartupPath") as string,
                gameKey?.GetValue("StartupParams") as string,
                File.Exists,
                out var plan,
                out failureReason))
        {
            return false;
        }

        try
        {
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = plan.ExecutablePath,
                Arguments = plan.Arguments,
                WorkingDirectory = Path.GetDirectoryName(plan.ExecutablePath),
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Windows 未返回游戏进程。");
            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"无法通过网易发烧游戏会话启动第五人格：{exception.Message}";
            return false;
        }
    }

    private static bool IsFeverRunning()
    {
        foreach (var process in Process.GetProcessesByName("FeverGamesInstaller"))
        {
            using (process)
                return true;
        }

        return false;
    }

    private static async Task HideFeverGamesWindowsAsync()
    {
        // The native launcher needs a moment to forward the command to its platform instance.
        // Hiding that window keeps the authenticated session and game process alive.
        foreach (var delay in new[] { 0, 100, 300, 700, 1500 })
        {
            if (delay > 0)
                await Task.Delay(delay).ConfigureAwait(false);

            foreach (var process in Process.GetProcessesByName("FeverGamesInstaller"))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                        _ = ShowWindow(process.MainWindowHandle, SwHide);
                }
            }
        }
    }

    private const int SwHide = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

internal sealed record FeverGamesLaunchPlan(string LauncherPath)
{
    internal const string AutoStartGameUri = "fevergames://mygame/?gameId=73&autoRun=1";

    public static bool TryCreate(
        string? protocolCommand,
        Func<string, bool> fileExists,
        out FeverGamesLaunchPlan plan,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        plan = null!;

        if (string.IsNullOrWhiteSpace(protocolCommand))
        {
            failureReason = "未找到网易发烧游戏启动协议。请先安装并登录网易发烧游戏。";
            return false;
        }

        var launcherPath = ExtractExecutablePath(protocolCommand);
        if (launcherPath is null || !fileExists(launcherPath))
        {
            failureReason = "网易发烧游戏启动器不存在或安装已损坏。请在启动器中修复安装。";
            return false;
        }

        plan = new FeverGamesLaunchPlan(launcherPath);
        failureReason = string.Empty;
        return true;
    }

    private static string? ExtractExecutablePath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? trimmed[..(executableEnd + 4)] : null;
    }
}

internal sealed record FeverGamesGameStartPlan(string ExecutablePath, string Arguments)
{
    public static bool TryCreate(
        string? installPath,
        string? startupPath,
        string? startupParams,
        Func<string, bool> fileExists,
        out FeverGamesGameStartPlan plan,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        plan = null!;

        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(startupPath))
        {
            failureReason = "未找到第五人格的网易发烧游戏安装信息。请在网易发烧游戏中修复安装。";
            return false;
        }

        var executablePath = Path.Combine(installPath, startupPath);
        if (!fileExists(executablePath))
        {
            failureReason = "第五人格启动程序不存在或安装已损坏。请在网易发烧游戏中修复安装。";
            return false;
        }

        plan = new FeverGamesGameStartPlan(executablePath, startupParams ?? string.Empty);
        failureReason = string.Empty;
        return true;
    }
}
