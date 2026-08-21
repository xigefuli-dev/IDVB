using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace IDVBuff.Features.Maps;

public sealed record GameIntegrityStatus(
    bool GameIsRunning,
    bool CurrentProcessIsElevated,
    bool GameProcessIsElevated,
    bool RequiresElevation,
    string Message);

/// <summary>Compares the integrity levels of Identity Vision Bridge and the running game.</summary>
public static class GameProcessIntegrityService
{
    private const string GameProcessName = "dwrg";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    public static GameIntegrityStatus Check()
    {
        var currentLevel = TryGetProcessIntegrityLevel(
            Process.GetCurrentProcess().Handle,
            ownsProcessHandle: false);
        var currentElevated = currentLevel >= SecurityMandatoryHighRid;
        var gameLevels = new List<int>();
        foreach (var process in Process.GetProcessesByName(GameProcessName))
        {
            using (process)
            {
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)process.Id);
                if (handle == IntPtr.Zero)
                    continue;
                var level = TryGetProcessIntegrityLevel(handle, ownsProcessHandle: true);
                if (level > 0)
                    gameLevels.Add(level);
            }
        }

        if (gameLevels.Count == 0)
        {
            return new GameIntegrityStatus(
                false,
                currentElevated,
                false,
                false,
                "尚未检测到 dwrg.exe。");
        }

        var gameElevated = gameLevels.Max() >= SecurityMandatoryHighRid;
        var requiresElevation = gameLevels.Max() > currentLevel;
        return new GameIntegrityStatus(
            true,
            currentElevated,
            gameElevated,
            requiresElevation,
            requiresElevation
                ? "游戏权限高于 Identity Vision Bridge，游戏内键盘和鼠标绑定无法可靠触发。请以管理员权限重启 Identity Vision Bridge。"
                : "Identity Vision Bridge 与游戏权限级别兼容，游戏内热键可用。");
    }

    public static bool TryRestartElevated(out string failureReason)
    {
        failureReason = string.Empty;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            failureReason = "无法确定当前程序路径，不能请求管理员重启。";
            return false;
        }

        try
        {
            var arguments = Environment.GetCommandLineArgs()
                .Skip(1)
                .Select(QuoteArgument);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = string.Join(" ", arguments),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory
            });
            if (process is null)
            {
                failureReason = "管理员进程未能启动。";
                return false;
            }
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            failureReason = "已取消管理员权限请求；当前程序会继续运行，但游戏内热键仍不可用。";
            return false;
        }
        catch (Exception exception)
        {
            failureReason = $"管理员重启失败：{exception.Message}";
            return false;
        }
    }

    private static int TryGetProcessIntegrityLevel(IntPtr processHandle, bool ownsProcessHandle)
    {
        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var token))
                return 0;
            try
            {
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var length);
                if (length <= 0)
                    return 0;
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, length, out _))
                        return 0;
                    var sid = Marshal.ReadIntPtr(buffer);
                    var count = GetSidSubAuthorityCount(sid);
                    if (count == IntPtr.Zero)
                        return 0;
                    var subAuthorityCount = Marshal.ReadByte(count);
                    var rid = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
                    return rid == IntPtr.Zero ? 0 : Marshal.ReadInt32(rid);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            if (ownsProcessHandle)
                CloseHandle(processHandle);
        }
    }

    private static string QuoteArgument(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;

    private const int SecurityMandatoryHighRid = 0x00003000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
/*
 * 文件职责：GameProcessIntegrityService。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
