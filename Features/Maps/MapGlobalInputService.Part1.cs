using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;
using IDVBuff.PluginHostMessages;
namespace IDVBuff.Features.Maps;
/// <summary>Pass-through global input bindings with keyboard polling for games that bypass hooks.</summary>
public sealed partial class MapGlobalInputService : IDisposable
{

    private static readonly object s_activeHooksLock = new();
    private static readonly HashSet<IntPtr> s_activeHooks = [];

    static MapGlobalInputService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyUnhookAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyUnhookAll();
    }

    /// <summary>
    /// 紧急卸载所有已安装的操作系统级低级钩子，防止进程异常退出或卡死时残留钩子导致整台电脑严重卡死。
    /// </summary>
    public static void EmergencyUnhookAll()
    {
        lock (s_activeHooksLock)
        {
            foreach (var hook in s_activeHooks)
            {
                if (hook != IntPtr.Zero)
                {
                    try
                    {
                        UnhookWindowsHookEx(hook);
                    }
                    catch
                    {
                        // 忽略卸载过程中的任何异常
                    }
                }
            }
            s_activeHooks.Clear();
        }
    }

    private static void TrackHook(IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            lock (s_activeHooksLock)
            {
                s_activeHooks.Add(hook);
            }
        }
    }

    private static void UntrackHook(IntPtr hook)
    {
        if (hook != IntPtr.Zero)
        {
            lock (s_activeHooksLock)
            {
                s_activeHooks.Remove(hook);
            }
        }
    }

    private void StartHookThread(bool installKeyboardHook, bool installMouseHook)
    {
        if (!installKeyboardHook && !installMouseHook)
            return;

        using var started = new ManualResetEventSlim();
        Exception? startupException = null;
        var thread = new Thread(() =>
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();
                // Force creation of this thread's Win32 message queue before
                // publishing its ID. This makes immediate reconfiguration or
                // disposal able to deliver WM_QUIT reliably.
                PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                var module = GetModuleHandle(null);
                if (installKeyboardHook)
                {
                    _keyboardHook = SetWindowsHookEx(
                        WhKeyboardLl, _keyboardProc, module, 0);
                    if (_keyboardHook == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "无法注册全局键盘监听。");
                    TrackHook(_keyboardHook);
                }
                if (installMouseHook)
                {
                    _mouseHook = SetWindowsHookEx(
                        WhMouseLl, _mouseProc, module, 0);
                    if (_mouseHook == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "无法注册全局鼠标按键监听。");
                    TrackHook(_mouseHook);
                }

                using var latencyReporter = new Timer(_ => ReportInputHookLatency(), null, 5000, 5000);
                started.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception exception)
            {
                startupException = exception;
                started.Set();
            }
            finally
            {
                if (_keyboardHook != IntPtr.Zero)
                {
                    UntrackHook(_keyboardHook);
                    UnhookWindowsHookEx(_keyboardHook);
                }
                if (_mouseHook != IntPtr.Zero)
                {
                    UntrackHook(_mouseHook);
                    UnhookWindowsHookEx(_mouseHook);
                }
                _keyboardHook = IntPtr.Zero;
                _mouseHook = IntPtr.Zero;
                _hookThreadId = 0;
            }
        })
        {
            IsBackground = true,
            Name = "IDVB global input hooks"
        };

        lock (_hookLifecycleLock)
            _hookThread = thread;
        thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("全局输入监听线程启动超时。");
        if (startupException is not null)
        {
            thread.Join();
            throw startupException;
        }
    }

    private static bool IsAnyKeyDown(params int[] keys) =>
        keys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static void EnsureDistinctBindings(params MapInputBinding[] bindings)
    {
        var configured = bindings.Where(binding => binding.IsConfigured).ToArray();
        for (var left = 0; left < configured.Length - 1; left++)
        {
            for (var right = left + 1; right < configured.Length; right++)
            {
                if (configured[left].Equals(configured[right]))
                    throw new InvalidOperationException("全局操作不能使用同一个按键。");
            }
        }
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_mouseHook, code, wParam, lParam);
        var started = Stopwatch.GetTimestamp();
        var arrival = unchecked((uint)Environment.TickCount);
        var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
        if (!IsMarkedInjectedMouse(mouse))
        {
            var message = (uint)wParam.ToInt64();
            if (message == WmMouseWheel)
            {
                var delta = (short)((mouse.MouseData >> 16) & 0xFFFF);
                if (delta != 0)
                    DispatchMouseWheel(new MouseWheelInputEventArgs(
                        Stopwatch.GetTimestamp(),
                        delta,
                        capsHeld: IsKeyDown(CapsLockVirtualKey),
                        pluginBindingStates: SnapshotPressedPluginBindings()));
            }

            if (TryGetMouseButton(message, lParam, out var button, out var isDown))
            {
                var timestamp = Stopwatch.GetTimestamp();
                var invoked = new MapInputInvokedEventArgs(timestamp);
                if (isDown
                    && _quickScan.Kind == MapInputBindingKind.Mouse
                    && _quickScan.MouseButton == button)
                    DispatchInput(invoked, "mouse", _quickScan.DisplayName,
                        "quick-scan", () => QuickScanInvoked?.Invoke(this, invoked));
                if (isDown
                    && _overlayToggle.Kind == MapInputBindingKind.Mouse
                    && _overlayToggle.MouseButton == button)
                    DispatchInput(invoked, "mouse", _overlayToggle.DisplayName,
                        "overlay-toggle", () => OverlayToggleInvoked?.Invoke(this, invoked));
                if (isDown
                    && _manualRecognition.Kind == MapInputBindingKind.Mouse
                    && _manualRecognition.MouseButton == button)
                    DispatchInput(invoked, "mouse", _manualRecognition.DisplayName,
                        "manual-recognition", () => ManualRecognitionInvoked?.Invoke(this, invoked));
                if (isDown
                    && _gameMapToggle.Kind == MapInputBindingKind.Mouse
                    && _gameMapToggle.MouseButton == button)
                    DispatchInput(invoked, "mouse", _gameMapToggle.DisplayName,
                        "game-map-toggle", () => GameMapToggleInvoked?.Invoke(this, invoked));
                if (isDown
                    && _controlPanelToggle.Kind == MapInputBindingKind.Mouse
                    && _controlPanelToggle.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _controlPanelToggle.DisplayName,
                        "control-panel-toggle",
                        () => ControlPanelToggleInvoked?.Invoke(this, invoked));
                }
                if (isDown
                    && _switchFloor.Kind == MapInputBindingKind.Mouse
                    && _switchFloor.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _switchFloor.DisplayName,
                        "switch-floor", () => SwitchFloorInvoked?.Invoke(this, invoked));
                }
                if (isDown
                    && _saveMapCache.Kind == MapInputBindingKind.Mouse
                    && _saveMapCache.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _saveMapCache.DisplayName,
                        "save-map-cache",
                        () => SaveMapCacheInvoked?.Invoke(this, invoked));
                }
                if (isDown
                    && _restMapDisplay.Kind == MapInputBindingKind.Mouse
                    && _restMapDisplay.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _restMapDisplay.DisplayName,
                        "rest-map-display",
                        () => RestMapDisplayInvoked?.Invoke(this, invoked));
                }
                if (isDown
                    && _matchStateToggle.Kind == MapInputBindingKind.Mouse
                    && _matchStateToggle.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _matchStateToggle.DisplayName,
                        "match-state-toggle",
                        () => MatchStateToggleInvoked?.Invoke(this, invoked));
                }

                DispatchPluginMouseInput(button, timestamp, isDown);
            }
        }
        if ((mouse.Flags & 1) == 0)
            _mouseLatency.Record(mouse.Time, arrival, Stopwatch.GetTimestamp() - started);
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void UnregisterBindings()
    {
        Timer? keyboardPoller;
        lock (_keyboardStateLock)
        {
            _keyboardBindingsActive = false;
            _keyboardPollGeneration++;
            keyboardPoller = _keyboardPoller;
            _keyboardPoller = null;
            _keyboardEdges.Clear();
        }
        keyboardPoller?.Dispose();
        Thread? hookThread;
        uint hookThreadId;
        lock (_hookLifecycleLock)
        {
            hookThread = _hookThread;
            hookThreadId = _hookThreadId;
            _hookThread = null;
        }
        if (hookThread is not null)
        {
            if (hookThreadId != 0)
                PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (hookThread != Thread.CurrentThread)
                hookThread.Join(TimeSpan.FromSeconds(2));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        lock (_keyboardStateLock)
            _pluginBindings.Clear();
        UnregisterBindings();
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
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;

        public static NativeInput CreateKeyUp(ushort virtualKey, bool extended) => new()
        {
            Type = InputKeyboard,
            Data = new NativeInputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = KeyeventfKeyup | (extended ? 0x0001u : 0u),
                    ExtraInfo = ReleaseInputMarker
                }
            }
        };

        public static NativeInput CreateMouse(uint flags, uint mouseData = 0) => new()
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = flags,
                    MouseData = mouseData,
                    ExtraInfo = ReleaseInputMarker
                }
            }
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
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
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamL;
        public ushort ParamH;
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
    private struct NativePoint
    {
        public int X;
        public int Y;
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

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(
        uint inputCount,
        [In] NativeInput[] inputs,
        int inputSize);
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
}
