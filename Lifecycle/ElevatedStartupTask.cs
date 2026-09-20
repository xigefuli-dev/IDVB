using System.Diagnostics;

namespace IDVBuff.Lifecycle;

public static class ElevatedStartupTask
{
    private const string TaskName = "Identity Vision Bridge";

    public static async Task SetEnabledAsync(bool enabled)
    {
        var arguments = enabled
            ? $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"\\\"{Environment.ProcessPath}\\\" --startup\""
            : $"/Delete /TN \"{TaskName}\" /F";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法启动 Windows 计划任务管理器。");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(enabled
                ? "未能创建管理员自启动任务。请确认 UAC 提示后重试。"
                : "未能删除管理员自启动任务。请确认 UAC 提示后重试。");
    }
}
