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

    public static bool TryLaunch(
        out string failureReason,
        FeverAccountProfile? explicitAccount = null,
        string? explicitTicket = null)
    {
        // 1. Prevent duplicate launch if Identity V is already running
        if (Process.GetProcessesByName("dwrg").Length > 0)
        {
            failureReason = "第五人格正在运行中，请勿重复启动。";
            return false;
        }

        var targetAccount = explicitAccount ?? FeverAccountStore.Instance.GetActiveAccount();

        // 2. Terminate background Fever processes to prevent window handle collision on LHMW_FG_Main
        FeverAccountStore.StopFeverProcesses();

        // 3. If long-term account, deploy credentials to Windows Registry
        if (targetAccount is not null && targetAccount.IsLongTerm && !string.IsNullOrEmpty(targetAccount.FeverToken))
        {
            FeverAccountStore.WriteFeverRegistryCredentials(targetAccount.FeverToken, targetAccount.FeverSdkuid);
        }

        // 4. Prepare IPC bridge with explicit ticket or account ticket
        var ticket = explicitTicket;
        if (string.IsNullOrWhiteSpace(ticket) && targetAccount is not null)
        {
            ticket = FeverAccountStore.Instance.GetAccountTicket(targetAccount.Id);
        }
        if (string.IsNullOrWhiteSpace(ticket))
        {
            ticket = FeverAccountStore.Instance.GetActiveAccountTicket();
        }

        if (string.IsNullOrWhiteSpace(ticket))
        {
            failureReason = "未找到有效的登录凭据，请先在官服账号管理中登录。";
            return false;
        }

        FeverIpcBridge.Instance.Start(ticket.Trim());

        // 5. Resolve Identity V game path from registry
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

        // 6. Directly start the game with --start_from_launcher=1
        try
        {
            var arguments = string.IsNullOrWhiteSpace(plan.Arguments)
                ? "--start_from_launcher=1"
                : plan.Arguments;

            _ = Process.Start(new ProcessStartInfo
            {
                FileName = plan.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(plan.ExecutablePath),
                UseShellExecute = false
            }) ?? throw new InvalidOperationException("Windows 未返回游戏进程。");

            failureReason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failureReason = $"无法启动第五人格：{exception.Message}";
            return false;
        }
    }
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
