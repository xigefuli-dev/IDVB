using System.Runtime.InteropServices;
using System.Text;

namespace IDVBuff.Features.GameLaunch;

public static partial class FeverLoginService
{
    private static IntPtr CreateHostWindow()
    {
        try
        {
            var hInstance = GetModuleHandleW(null);
            var className = "IDVBMpayHostWnd";

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate<WndProcDelegate>(DefWindowProcW),
                hInstance = hInstance,
                lpszClassName = className
            };

            RegisterClassExW(ref wc);

            var hwnd = CreateWindowExW(
                0x00000080 /* WS_EX_TOOLWINDOW */,
                className,
                "IDVB MPay Host",
                0,
                100, 100, 1, 1,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, 1 /* SW_SHOWNORMAL */);
            }

            return hwnd;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FeverLogin] CreateHostWindow failed: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    private static void MonitorAndActivateMpayWindows(CancellationToken ct)
    {
        var currentPid = (uint)Environment.ProcessId;
        for (int i = 0; i < 30; i++)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == currentPid && IsWindowVisible(hwnd))
                    {
                        var sb = new StringBuilder(256);
                        GetClassNameW(hwnd, sb, sb.Capacity);
                        var clsName = sb.ToString();
                        if (clsName.StartsWith("MPAY_", StringComparison.OrdinalIgnoreCase))
                        {
                            BringWindowToTop(hwnd);
                            SetForegroundWindow(hwnd);
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            Thread.Sleep(150);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseInterfaceDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetResPathDelegate(IntPtr instance, int type, IntPtr path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOptionDelegate(IntPtr name, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(
        IntPtr instance, IntPtr gameId, IntPtr reserved1, IntPtr appChannel,
        IntPtr reserved2, IntPtr reserved3, IntPtr callback, int flag);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoginDelegate(IntPtr instance, int mode, int style);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetUserTicketDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NoArgsCallback(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoginFinishCallback(IntPtr thisPtr, uint code, IntPtr extra);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TicketResultCallback(IntPtr thisPtr, int code, IntPtr ticket, IntPtr extra);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW([In] ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
