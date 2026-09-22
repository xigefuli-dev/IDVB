using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IDVBuff.Plugins.IdvLogin;

internal static class LoginProcessReplacement
{
    public static async Task StopVerifiedListenerAsync(ProcessStartInfo configuredStart, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException();
        var pid = FindListener();
        using var process = Process.GetProcessById(pid);
        // Pin the process handle before checking identity, so PID reuse cannot
        // turn the verified target into a different process at termination time.
        var handle = process.SafeHandle;
        var path = new StringBuilder(32768);
        var length = path.Capacity;
        if (!QueryFullProcessImageName(handle.DangerousGetHandle(), 0, path, ref length) ||
            !string.Equals(Path.GetFullPath(path.ToString()), Path.GetFullPath(configuredStart.FileName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("监听进程不属于配置的 idv-login 安装");
        token.ThrowIfCancellationRequested();
        process.CloseMainWindow();
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(token);
        grace.CancelAfter(TimeSpan.FromSeconds(2));
        try { await process.WaitForExitAsync(grace.Token); return; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
        // Only this verified listener, never its tree or other Python processes.
        process.Kill();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    private static int FindListener()
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (size < 4) throw new InvalidOperationException();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) throw new InvalidOperationException();
            var count = Marshal.ReadInt32(buffer);
            var owners = new HashSet<int>();
            for (var index = 0; index < count; index++)
            {
                var offset = checked(4 + index * 24);
                if (offset + 24 > size) throw new InvalidOperationException();
                var address = unchecked((uint)Marshal.ReadInt32(buffer, offset + 4));
                var port = unchecked((uint)Marshal.ReadInt32(buffer, offset + 8));
                var hostPort = ((port & 255) << 8) | ((port >> 8) & 255);
                if (hostPort == 443 && (address == 0 || address == 0x0100007f))
                    owners.Add(Marshal.ReadInt32(buffer, offset + 20));
            }
            return owners.Count == 1 ? owners.Single() : throw new InvalidOperationException("无法唯一确定 idv-login 进程");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int length);
}
