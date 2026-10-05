using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 鼠标 / 键盘注入。所有合成输入都打上宿主的注入标记
/// （<see cref="InputInjectionMarkers.HostGeneratedInput"/>），
/// 否则会被宿主的全局输入钩子当成用户真实操作。
/// </summary>
internal static class NativeInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXdown = 0x0080;
    private const uint MouseeventfXup = 0x0100;
    private static readonly IntPtr InjectionMarker = new(InputInjectionMarkers.HostGeneratedInput);
    private static readonly object SendGate = new();

    public static bool IsForegroundWindow(IntPtr window) =>
        window != IntPtr.Zero && GetForegroundWindow() == window;

    public static bool TryGetForegroundGameWindow(out IntPtr window)
    {
        window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
            return false;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "dwrg", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetClientBounds(IntPtr window, out PluginClientBounds bounds)
    {
        bounds = default;
        if (window == IntPtr.Zero || !GetClientRect(window, out var client))
            return false;

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0)
            return false;

        var origin = new NativePoint { X = client.Left, Y = client.Top };
        if (!ClientToScreen(window, ref origin))
            return false;

        bounds = new PluginClientBounds(origin.X, origin.Y, width, height);
        return true;
    }

    public static void SetLeftButton(bool down) => SendMouse(down ? MouseeventfLeftdown : MouseeventfLeftup, 0);

    public static void MoveCursorTo(int x, int y)
    {
        lock (SendGate)
        {
            if (!SetCursorPos(x, y))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标指针。");
        }
    }

    /// <summary>带缓动的平滑位移，避免瞬移式点击过于机械。</summary>
    public static void MoveSmoothly(int x, int y, int durationMilliseconds, CancellationToken cancellationToken)
    {
        if (!GetCursorPos(out var start))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前鼠标位置。");

        var distance = Math.Sqrt(Math.Pow(x - start.X, 2) + Math.Pow(y - start.Y, 2));
        var steps = Math.Clamp((int)Math.Ceiling(distance / 18d), 4, 40);
        var duration = Math.Clamp(durationMilliseconds, 20, 1000);
        var stopwatch = Stopwatch.StartNew();

        for (var step = 1; step <= steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = step / (double)steps;
            var eased = progress * progress * (3 - 2 * progress);
            lock (SendGate)
            {
                if (!SetCursorPos(
                        (int)Math.Round(start.X + ((x - start.X) * eased)),
                        (int)Math.Round(start.Y + ((y - start.Y) * eased))))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标指针。");
                }
            }

            var remaining = (duration * progress) - stopwatch.Elapsed.TotalMilliseconds;
            if (remaining > 0)
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remaining));
        }
    }

    public static void ClickLeft(int holdMilliseconds)
    {
        SendMouse(MouseeventfLeftdown, 0);
        try
        {
            Thread.Sleep(Math.Clamp(holdMilliseconds, 1, 500));
        }
        finally
        {
            SendMouse(MouseeventfLeftup, 0);
        }
    }

    /// <summary>按下并抬起一个键盘键，用于「按 Tab 开 / 关背包」这类固定动作。</summary>
    public static void InjectKey(uint virtualKey, int holdMilliseconds)
    {
        SendKeyboard(virtualKey, up: false);
        try
        {
            Thread.Sleep(Math.Clamp(holdMilliseconds, 1, 500));
        }
        finally
        {
            SendKeyboard(virtualKey, up: true);
        }
    }

    /// <summary>按下并抬起一个插件绑定（键盘组合或鼠标键）。</summary>
    public static void InjectBinding(PluginInputBinding binding, int holdMilliseconds)
    {
        SceneQuickActionsBindingExecutor.Execute(binding, holdMilliseconds, SendKeyboard,
            (button, up) =>
            {
                var flags = GetMouseFlags(button);
                SendMouse(up ? flags.Up : flags.Down, flags.Data);
            }, Thread.Sleep);
    }

    public static void Sleep(int milliseconds, CancellationToken cancellationToken) =>
        cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));

    private static (uint Down, uint Up, uint Data) GetMouseFlags(PluginMouseButton button) =>
        button switch
        {
            PluginMouseButton.Left => (MouseeventfLeftdown, MouseeventfLeftup, 0u),
            PluginMouseButton.Right => (MouseeventfRightdown, MouseeventfRightup, 0u),
            PluginMouseButton.Middle => (MouseeventfMiddledown, MouseeventfMiddleup, 0u),
            PluginMouseButton.XButton1 => (MouseeventfXdown, MouseeventfXup, 1u << 16),
            PluginMouseButton.XButton2 => (MouseeventfXdown, MouseeventfXup, 2u << 16),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };

    private static void SendKeyboard(uint virtualKey, bool up)
    {
        if (virtualKey == 0 || virtualKey > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(virtualKey));

        var input = new NativeInputStructure
        {
            Type = InputKeyboard,
            Data = new NativeInputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Flags = up ? KeyeventfKeyup : 0,
                    ExtraInfo = InjectionMarker
                }
            }
        };

        lock (SendGate)
        {
            if (SendInput(1, [input], Marshal.SizeOf<NativeInputStructure>()) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入键盘输入。");
        }
    }

    private static void SendMouse(uint flags, uint mouseData)
    {
        var input = new NativeInputStructure
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    MouseData = mouseData,
                    Flags = flags,
                    ExtraInfo = InjectionMarker
                }
            }
        };

        lock (SendGate)
        {
            if (SendInput(1, [input], Marshal.SizeOf<NativeInputStructure>()) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入鼠标输入。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInputStructure
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInputStructure[] inputs, int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
}
