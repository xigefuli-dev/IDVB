using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.AutoClicker;
/// <summary>
/// 连点器的 Win32 实现：物理鼠标右键在短按时正常透传（不影响日常右键菜单等）；
/// 一旦按住达到长按阈值，钩子便接管——吞掉物理鼠标右键，
/// 用 SendInput 原子注入 F 按下/抬起；
/// 松开即停止，结束路径强制补发 F 抬起。
/// </summary>
public sealed partial class AutoClickerService : IDisposable
{

    /// <summary>接管时结束此前透传的物理触发键按下。</summary>
    private bool SendTriggerUp()
    {
        PluginInputBinding trigger;
        lock (_sync)
            trigger = _triggerBinding;
        var input = new NativeInput
        {
            Type = trigger.Kind == PluginInputBindingKind.Mouse
                ? InputMouse
                : InputKeyboard,
            Data = trigger.Kind == PluginInputBindingKind.Mouse
                ? new NativeInputUnion
                {
                    Mouse = new MouseInput
                    {
                        Flags = GetMouseUpFlags(trigger.MouseButton),
                        MouseData = GetMouseData(trigger.MouseButton),
                        ExtraInfo = InputInjectionMarker
                    }
                }
                : new NativeInputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = (ushort)trigger.VirtualKey,
                        Flags = KeyeventfKeyup,
                        ExtraInfo = InputInjectionMarker
                    }
                }
        };
        lock (_sendInputSync)
            return SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1;
    }

    /// <summary>一次连点中的 F↓。</summary>
    private bool SendKeyDown(int sessionGeneration) =>
        SendKey(sessionGeneration, keyUp: false);

    /// <summary>一次连点中的 F↑。</summary>
    private bool SendKeyUp(int sessionGeneration) =>
        SendKey(sessionGeneration, keyUp: true);

    /// <summary>
    /// 发送一次 F 按下/抬起。保留原 <c>SendClick</c> 的状态守卫：会话失效
    /// 即返回 false → 调用方 break → <see cref="ClickingLoop"/> 的 finally
    /// 与 <see cref="HandlePhysicalButtonUp"/> 都会兜底补发 F↑，绝无卡键。
    /// </summary>
    private bool SendKey(int sessionGeneration, bool keyUp)
    {
        var input = new[]
        {
            new NativeInput
            {
                Type = InputKeyboard,
                Data = new NativeInputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = _outputVirtualKey,
                        Flags = keyUp ? KeyeventfKeyup : 0,
                        ExtraInfo = InputInjectionMarker
                    }
                }
            },
        };
        lock (_sendInputSync)
        {
            lock (_sync)
            {
                if (!_started
                    || !_physicalButtonDown
                    || !_clicking
                    || _pressGeneration != sessionGeneration)
                {
                    return false;
                }
            }
            return SendInput((uint)input.Length, input, Marshal.SizeOf<NativeInput>())
                == (uint)input.Length;
        }
    }

    /// <summary>
    /// Explicitly releases F. This closes a partially delivered SendInput batch
    /// and makes right-button termination deterministic.
    /// </summary>
    private void SendReleaseSignals()
    {
        var inputs = new[]
        {
            new NativeInput
            {
                Type = InputKeyboard,
                Data = new NativeInputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = _outputVirtualKey,
                        Flags = KeyeventfKeyup,
                        ExtraInfo = InputInjectionMarker
                    }
                }
            },
        };
        lock (_sendInputSync)
            _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static void WaitUntil(long targetTicks)
    {
        // timeBeginPeriod(1) 生效后 Thread.Sleep(1) 约 1-2ms。不要在最后
        // 1ms 忙等；短周期连点下这会长期占满一个 CPU 核。
        while (Stopwatch.GetTimestamp() < targetTicks)
        {
            var remainingMs =
                (targetTicks - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 0.2)
                Thread.Sleep(Math.Max(1, (int)Math.Min(remainingMs, 2.0)));
        }
    }

    private static bool TryGetMouseButton(
        uint message,
        IntPtr lParam,
        out PluginMouseButton button,
        out bool isDown)
    {
        button = PluginMouseButton.Left;
        isDown = true;
        switch (message)
        {
            case WmLButtonDown: button = PluginMouseButton.Left; return true;
            case WmLButtonUp: button = PluginMouseButton.Left; isDown = false; return true;
            case WmRButtonDown: button = PluginMouseButton.Right; return true;
            case WmRButtonUp: button = PluginMouseButton.Right; isDown = false; return true;
            case WmMButtonDown: button = PluginMouseButton.Middle; return true;
            case WmMButtonUp: button = PluginMouseButton.Middle; isDown = false; return true;
            case WmXButtonDown:
                button = ReadXButton(lParam);
                return true;
            case WmXButtonUp:
                button = ReadXButton(lParam);
                isDown = false;
                return true;
            default:
                return false;
        }
    }

    private static PluginMouseButton ReadXButton(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam).MouseData;
        return ((data >> 16) & 0xFFFF) == 2
            ? PluginMouseButton.XButton2
            : PluginMouseButton.XButton1;
    }

    private static uint GetMouseUpFlags(PluginMouseButton button) => button switch
    {
        PluginMouseButton.Left => MouseeventfLeftup,
        PluginMouseButton.Right => MouseeventfRightup,
        PluginMouseButton.Middle => MouseeventfMiddleup,
        PluginMouseButton.XButton1 or PluginMouseButton.XButton2 => MouseeventfXup,
        _ => MouseeventfLeftup
    };

    private static uint GetMouseData(PluginMouseButton button) => button switch
    {
        PluginMouseButton.XButton1 => 1u << 16,
        PluginMouseButton.XButton2 => 2u << 16,
        _ => 0
    };

    private static bool AreRequiredModifiersDown(PluginInputModifiers modifiers)
    {
        if (modifiers.HasFlag(PluginInputModifiers.Control)
            && !IsAnyKeyDown(0x11, 0xA2, 0xA3))
            return false;
        if (modifiers.HasFlag(PluginInputModifiers.Alt)
            && !IsAnyKeyDown(0x12, 0xA4, 0xA5))
            return false;
        if (modifiers.HasFlag(PluginInputModifiers.Shift)
            && !IsAnyKeyDown(0x10, 0xA0, 0xA1))
            return false;
        if (modifiers.HasFlag(PluginInputModifiers.Windows)
            && !IsAnyKeyDown(0x5B, 0x5C))
            return false;
        return true;
    }

    private static bool IsAnyKeyDown(params int[] keys) =>
        keys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
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
    private struct MsLlHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(
        int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(
        int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount, [In] NativeInput[] inputs, int inputSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(
        uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(
        out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
