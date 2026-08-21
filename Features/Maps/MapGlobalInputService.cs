using Microsoft.UI.Dispatching;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Features.Maps;

public sealed class MapInputInvokedEventArgs(long timestamp) : EventArgs
{
    public long Timestamp { get; } = timestamp;
}

/// <summary>Pass-through global input bindings with keyboard polling for games that bypass hooks.</summary>
public sealed partial class MapGlobalInputService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint LlkhfInjected = 0x00000010;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXup = 0x0100;
    private static readonly IntPtr ReleaseInputMarker =
        new(InputInjectionMarkers.HostGeneratedInput);
    private const int KeyboardPollIntervalMilliseconds = 15;
    private const long DuplicateKeyDownSuppressionMilliseconds = 120;
    private const uint CapsLockVirtualKey = 0x14;

    private readonly DispatcherQueue _dispatcher;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly LowLevelMouseProc _mouseProc;
    private readonly object _keyboardStateLock = new();
    private readonly object _hookLifecycleLock = new();
    private readonly HashSet<uint> _pressedKeys = [];
    private readonly Dictionary<uint, long> _lastKeyDownAt = [];
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Timer? _keyboardPoller;
    private int _keyboardPollGeneration;
    private bool _keyboardBindingsActive;
    private MapInputBinding _quickScan = new();
    private MapInputBinding _overlayToggle = new();
    private MapInputBinding _manualRecognition = new();
    private MapInputBinding _gameMapToggle = new();
    private MapInputBinding _controlPanelToggle = new();
    private MapInputBinding _switchFloor = new();
    private MapInputBinding _saveMapCache = new();
    private bool _disposed;

    public MapGlobalInputService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    public event EventHandler<MapInputInvokedEventArgs>? QuickScanInvoked;
    public event EventHandler<MapInputInvokedEventArgs>? OverlayToggleInvoked;
    public event EventHandler<MapInputInvokedEventArgs>? ManualRecognitionInvoked;
    public event EventHandler<MapInputInvokedEventArgs>? GameMapToggleInvoked;
    public event EventHandler<MapInputInvokedEventArgs>? ControlPanelToggleInvoked;
    public event EventHandler<MapInputInvokedEventArgs>? SwitchFloorInvoked;
    public event EventHandler<MapInputInvokedEventArgs>? SaveMapCacheInvoked;
    public event EventHandler<MouseWheelInputEventArgs>? MouseWheelScrolled;

    public void ApplyBindings(
        MapInputBinding quickScan,
        MapInputBinding overlayToggle,
        MapInputBinding manualRecognition,
        MapInputBinding gameMapToggle,
        MapInputBinding controlPanelToggle,
        MapInputBinding switchFloor,
        MapInputBinding saveMapCache)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDistinctBindings(
            quickScan,
            overlayToggle,
            manualRecognition,
            gameMapToggle,
            controlPanelToggle,
            switchFloor,
            saveMapCache);
        UnregisterBindings();
        _quickScan = quickScan.Clone();
        _overlayToggle = overlayToggle.Clone();
        _manualRecognition = manualRecognition.Clone();
        _gameMapToggle = gameMapToggle.Clone();
        _controlPanelToggle = controlPanelToggle.Clone();
        _switchFloor = switchFloor.Clone();
        _saveMapCache = saveMapCache.Clone();
        try
        {
            var needsKeyboardHook = true;
            // 插件可以消费组合键滚轮。鼠标钩子因此即使当前没有鼠标按钮绑定
            // 也必须保持启用；滚轮事件只是观察，不会阻止原应用收到滚轮。
            var needsMouseHook = true;

            StartHookThread(needsKeyboardHook, needsMouseHook);
            StartKeyboardPolling();
        }
        catch
        {
            UnregisterBindings();
            throw;
        }
    }

    public void ClearBindings()
    {
        UnregisterBindings();
        _quickScan = new MapInputBinding();
        _overlayToggle = new MapInputBinding();
        _manualRecognition = new MapInputBinding();
        _gameMapToggle = new MapInputBinding();
        _controlPanelToggle = new MapInputBinding();
        _switchFloor = new MapInputBinding();
        _saveMapCache = new MapInputBinding();
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            // ReleaseAllPressedInputs uses SendInput. Do not let those synthetic
            // key-up messages clear the physical key state: the physical key may
            // still be held and polling would otherwise invoke the binding again.
            if ((keyboard.Flags & LlkhfInjected) == 0
                || keyboard.ExtraInfo != ReleaseInputMarker)
            {
                var message = (uint)wParam.ToInt64();
                if (message is WmKeyDown or WmSysKeyDown)
                    HandleKeyboardState(keyboard.VirtualKey, isDown: true);
                else if (message is WmKeyUp or WmSysKeyUp)
                    HandleKeyboardState(keyboard.VirtualKey, isDown: false);
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    /// <summary>
    /// Releases every currently pressed keyboard key and mouse button in the
    /// foreground application, matching the input handoff used by an in-game
    /// overlay when it takes focus from a game.
    /// </summary>
    public void ReleaseAllPressedInputs()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inputs = new List<NativeInput>(256);
        for (var virtualKey = 8; virtualKey <= byte.MaxValue; virtualKey++)
        {
            if ((GetAsyncKeyState(virtualKey) & 0x8000) == 0)
                continue;

            inputs.Add(NativeInput.CreateKeyUp(
                (ushort)virtualKey,
                IsExtendedKey((ushort)virtualKey)));
        }

        AddMouseReleaseIfPressed(inputs, 1, MouseeventfLeftup);
        AddMouseReleaseIfPressed(inputs, 2, MouseeventfRightup);
        AddMouseReleaseIfPressed(inputs, 4, MouseeventfMiddleup);
        AddMouseReleaseIfPressed(inputs, 5, MouseeventfXup, 1);
        AddMouseReleaseIfPressed(inputs, 6, MouseeventfXup, 2);

        if (inputs.Count == 0)
            return;

        _ = SendInput(
            (uint)inputs.Count,
            inputs.ToArray(),
            Marshal.SizeOf<NativeInput>());
    }

    private static void AddMouseReleaseIfPressed(
        ICollection<NativeInput> inputs,
        int virtualKey,
        uint flags,
        uint mouseData = 0)
    {
        if ((GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            inputs.Add(NativeInput.CreateMouse(flags, mouseData));
    }

    private static bool IsExtendedKey(ushort virtualKey) =>
        virtualKey is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27
            or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F
            or 0xA3 or 0xA5;

    private void StartKeyboardPolling()
    {
        lock (_keyboardStateLock)
        {
            _keyboardBindingsActive = true;
            InitializePressedKey(_quickScan);
            InitializePressedKey(_overlayToggle);
            InitializePressedKey(_manualRecognition);
            InitializePressedKey(_gameMapToggle);
            InitializePressedKey(_controlPanelToggle);
            InitializePressedKey(_switchFloor);
            InitializePressedKey(_saveMapCache);
            var generation = ++_keyboardPollGeneration;
            _keyboardPoller = new Timer(
                PollKeyboardBindings,
                generation,
                KeyboardPollIntervalMilliseconds,
                KeyboardPollIntervalMilliseconds);
        }
    }

    private void InitializePressedKey(MapInputBinding binding)
    {
        if (binding.Kind == MapInputBindingKind.Keyboard
            && IsKeyDown(binding.VirtualKey))
        {
            _pressedKeys.Add(binding.VirtualKey);
        }
    }

    private void PollKeyboardBindings(object? state)
    {
        if (state is not int generation)
            return;

        uint quickKey;
        uint overlayKey;
        uint manualKey;
        uint gameMapKey;
        uint controlPanelKey;
        uint switchFloorKey;
        uint saveMapCacheKey;
        lock (_keyboardStateLock)
        {
            if (!_keyboardBindingsActive
                || generation != _keyboardPollGeneration
                || _disposed)
            {
                return;
            }
            quickKey = _quickScan.Kind == MapInputBindingKind.Keyboard ? _quickScan.VirtualKey : 0;
            overlayKey = _overlayToggle.Kind == MapInputBindingKind.Keyboard ? _overlayToggle.VirtualKey : 0;
            manualKey = _manualRecognition.Kind == MapInputBindingKind.Keyboard
                ? _manualRecognition.VirtualKey
                : 0;
            gameMapKey = _gameMapToggle.Kind == MapInputBindingKind.Keyboard
                ? _gameMapToggle.VirtualKey
                : 0;
            controlPanelKey =
                _controlPanelToggle.Kind == MapInputBindingKind.Keyboard
                    ? _controlPanelToggle.VirtualKey
                    : 0;
            switchFloorKey =
                _switchFloor.Kind == MapInputBindingKind.Keyboard
                    ? _switchFloor.VirtualKey
                    : 0;
            saveMapCacheKey =
                _saveMapCache.Kind == MapInputBindingKind.Keyboard
                    ? _saveMapCache.VirtualKey
                    : 0;
        }

        if (quickKey != 0)
            HandleKeyboardState(quickKey, IsKeyDown(quickKey), generation);
        if (overlayKey != 0 && overlayKey != quickKey)
            HandleKeyboardState(overlayKey, IsKeyDown(overlayKey), generation);
        if (manualKey != 0 && manualKey != quickKey && manualKey != overlayKey)
            HandleKeyboardState(manualKey, IsKeyDown(manualKey), generation);
        if (gameMapKey != 0
            && gameMapKey != quickKey
            && gameMapKey != overlayKey
            && gameMapKey != manualKey)
        {
            HandleKeyboardState(gameMapKey, IsKeyDown(gameMapKey), generation);
        }
        if (controlPanelKey != 0
            && controlPanelKey != quickKey
            && controlPanelKey != overlayKey
            && controlPanelKey != manualKey
            && controlPanelKey != gameMapKey)
        {
            HandleKeyboardState(
                controlPanelKey,
                IsKeyDown(controlPanelKey),
                generation);
        }
        if (switchFloorKey != 0
            && switchFloorKey != quickKey
            && switchFloorKey != overlayKey
            && switchFloorKey != manualKey
            && switchFloorKey != gameMapKey
            && switchFloorKey != controlPanelKey)
        {
            HandleKeyboardState(
                switchFloorKey,
                IsKeyDown(switchFloorKey),
                generation);
        }
        if (saveMapCacheKey != 0
            && saveMapCacheKey != quickKey
            && saveMapCacheKey != overlayKey
            && saveMapCacheKey != manualKey
            && saveMapCacheKey != gameMapKey
            && saveMapCacheKey != controlPanelKey
            && saveMapCacheKey != switchFloorKey)
        {
            HandleKeyboardState(
                saveMapCacheKey,
                IsKeyDown(saveMapCacheKey),
                generation);
        }
    }

    private void HandleKeyboardState(uint key, bool isDown, int? expectedGeneration = null)
    {
        var invokeQuickScan = false;
        var invokeOverlayToggle = false;
        var invokeManualRecognition = false;
        var invokeGameMapToggle = false;
        var invokeControlPanelToggle = false;
        var invokeSwitchFloor = false;
        var invokeSaveMapCache = false;
        var invokeAlt = false;
        lock (_keyboardStateLock)
        {
            if (!_keyboardBindingsActive
                || (expectedGeneration is { } generation && generation != _keyboardPollGeneration))
            {
                return;
            }
            if (!isDown)
            {
                _pressedKeys.Remove(key);
                return;
            }
            if (!_pressedKeys.Add(key))
                return;

            var now = Environment.TickCount64;
            if (_lastKeyDownAt.TryGetValue(key, out var last)
                && now - last < DuplicateKeyDownSuppressionMilliseconds)
            {
                return;
            }
            _lastKeyDownAt[key] = now;
            invokeQuickScan = _quickScan.Kind == MapInputBindingKind.Keyboard
                && _quickScan.VirtualKey == key
                && AreRequiredModifiersDown(_quickScan.Modifiers);
            invokeOverlayToggle = _overlayToggle.Kind == MapInputBindingKind.Keyboard
                && _overlayToggle.VirtualKey == key
                && AreRequiredModifiersDown(_overlayToggle.Modifiers);
            invokeManualRecognition = _manualRecognition.Kind == MapInputBindingKind.Keyboard
                && _manualRecognition.VirtualKey == key
                && AreRequiredModifiersDown(_manualRecognition.Modifiers);
            invokeGameMapToggle = _gameMapToggle.Kind == MapInputBindingKind.Keyboard
                && _gameMapToggle.VirtualKey == key
                && AreRequiredModifiersDown(_gameMapToggle.Modifiers);
            invokeControlPanelToggle =
                _controlPanelToggle.Kind == MapInputBindingKind.Keyboard
                && _controlPanelToggle.VirtualKey == key
                && AreRequiredModifiersDown(_controlPanelToggle.Modifiers);
            invokeSwitchFloor = _switchFloor.Kind == MapInputBindingKind.Keyboard
                && _switchFloor.VirtualKey == key
                && AreRequiredModifiersDown(_switchFloor.Modifiers);
            invokeSaveMapCache = _saveMapCache.Kind == MapInputBindingKind.Keyboard
                && _saveMapCache.VirtualKey == key
                && AreRequiredModifiersDown(_saveMapCache.Modifiers);
            invokeAlt = key is 0x12 or 0xA4 or 0xA5;
        }

        var invoked = new MapInputInvokedEventArgs(Stopwatch.GetTimestamp());
        if (invokeQuickScan)
            DispatchInput(invoked, "keyboard", _quickScan.DisplayName,
                "quick-scan", () => QuickScanInvoked?.Invoke(this, invoked));
        if (invokeOverlayToggle)
            DispatchInput(invoked, "keyboard", _overlayToggle.DisplayName,
                "overlay-toggle", () => OverlayToggleInvoked?.Invoke(this, invoked));
        if (invokeManualRecognition)
            DispatchInput(invoked, "keyboard", _manualRecognition.DisplayName,
                "manual-recognition", () => ManualRecognitionInvoked?.Invoke(this, invoked));
        if (invokeGameMapToggle)
            DispatchInput(invoked, "keyboard", _gameMapToggle.DisplayName,
                "game-map-toggle", () => GameMapToggleInvoked?.Invoke(this, invoked));
        if (invokeControlPanelToggle)
        {
            DispatchInput(invoked, "keyboard", _controlPanelToggle.DisplayName,
                "control-panel-toggle",
                () => ControlPanelToggleInvoked?.Invoke(this, invoked));
        }
        if (invokeSwitchFloor)
            DispatchInput(invoked, "keyboard", _switchFloor.DisplayName,
                "switch-floor", () => SwitchFloorInvoked?.Invoke(this, invoked));
        if (invokeSaveMapCache)
            DispatchInput(invoked, "keyboard", _saveMapCache.DisplayName,
                "save-map-cache", () => SaveMapCacheInvoked?.Invoke(this, invoked));
        if (invokeAlt)
            DispatchInput(invoked, "keyboard", "Alt", "alt",
                () => AltInvoked?.Invoke(this, invoked));
    }

    private static bool IsKeyDown(uint key) =>
        (GetAsyncKeyState((int)key) & 0x8000) != 0;

    private static bool AreRequiredModifiersDown(MapInputModifiers modifiers)
    {
        if (modifiers.HasFlag(MapInputModifiers.Control)
            && !IsAnyKeyDown(0x11, 0xA2, 0xA3))
        {
            return false;
        }
        if (modifiers.HasFlag(MapInputModifiers.Alt)
            && !IsAnyKeyDown(0x12, 0xA4, 0xA5))
        {
            return false;
        }
        if (modifiers.HasFlag(MapInputModifiers.Shift)
            && !IsAnyKeyDown(0x10, 0xA0, 0xA1))
        {
            return false;
        }
        if (modifiers.HasFlag(MapInputModifiers.Windows)
            && !IsAnyKeyDown(0x5B, 0x5C))
        {
            return false;
        }
        return true;
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
                }
                if (installMouseHook)
                {
                    _mouseHook = SetWindowsHookEx(
                        WhMouseLl, _mouseProc, module, 0);
                    if (_mouseHook == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(),
                            "无法注册全局鼠标按键监听。");
                }

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
                    UnhookWindowsHookEx(_keyboardHook);
                if (_mouseHook != IntPtr.Zero)
                    UnhookWindowsHookEx(_mouseHook);
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
        if (code >= 0 && !IsMarkedInjectedMouse(lParam))
        {
            var message = (uint)wParam.ToInt64();
            if (message == WmMouseWheel && IsKeyDown(CapsLockVirtualKey))
            {
                var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                var delta = (short)((mouse.MouseData >> 16) & 0xFFFF);
                if (delta != 0)
                    DispatchMouseWheel(new MouseWheelInputEventArgs(
                        Stopwatch.GetTimestamp(), delta, capsHeld: true));
            }

            if (TryGetMouseButton(message, lParam, out var button))
            {
                var invoked = new MapInputInvokedEventArgs(Stopwatch.GetTimestamp());
                if (_quickScan.Kind == MapInputBindingKind.Mouse && _quickScan.MouseButton == button)
                    DispatchInput(invoked, "mouse", _quickScan.DisplayName,
                        "quick-scan", () => QuickScanInvoked?.Invoke(this, invoked));
                if (_overlayToggle.Kind == MapInputBindingKind.Mouse && _overlayToggle.MouseButton == button)
                    DispatchInput(invoked, "mouse", _overlayToggle.DisplayName,
                        "overlay-toggle", () => OverlayToggleInvoked?.Invoke(this, invoked));
                if (_manualRecognition.Kind == MapInputBindingKind.Mouse && _manualRecognition.MouseButton == button)
                    DispatchInput(invoked, "mouse", _manualRecognition.DisplayName,
                        "manual-recognition", () => ManualRecognitionInvoked?.Invoke(this, invoked));
                if (_gameMapToggle.Kind == MapInputBindingKind.Mouse && _gameMapToggle.MouseButton == button)
                    DispatchInput(invoked, "mouse", _gameMapToggle.DisplayName,
                        "game-map-toggle", () => GameMapToggleInvoked?.Invoke(this, invoked));
                if (_controlPanelToggle.Kind == MapInputBindingKind.Mouse
                    && _controlPanelToggle.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _controlPanelToggle.DisplayName,
                        "control-panel-toggle",
                        () => ControlPanelToggleInvoked?.Invoke(this, invoked));
                }
                if (_switchFloor.Kind == MapInputBindingKind.Mouse
                    && _switchFloor.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _switchFloor.DisplayName,
                        "switch-floor", () => SwitchFloorInvoked?.Invoke(this, invoked));
                }
                if (_saveMapCache.Kind == MapInputBindingKind.Mouse
                    && _saveMapCache.MouseButton == button)
                {
                    DispatchInput(invoked, "mouse", _saveMapCache.DisplayName,
                        "save-map-cache",
                        () => SaveMapCacheInvoked?.Invoke(this, invoked));
                }
            }
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static bool TryGetMouseButton(uint message, IntPtr lParam, out MapMouseButton button)
    {
        button = MapMouseButton.Left;
        switch (message)
        {
            case WmLButtonDown: button = MapMouseButton.Left; return true;
            case WmRButtonDown: button = MapMouseButton.Right; return true;
            case WmMButtonDown: button = MapMouseButton.Middle; return true;
            case WmXButtonDown:
                var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam).MouseData;
                button = ((data >> 16) & 0xFFFF) == 2 ? MapMouseButton.XButton2 : MapMouseButton.XButton1;
                return true;
            default:
                return false;
        }
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
            _pressedKeys.Clear();
            _lastKeyDownAt.Clear();
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
/*
 * 文件职责：MapGlobalInputService。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
