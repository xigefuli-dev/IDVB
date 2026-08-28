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
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmXButtonDown = 0x020B;
    private const uint WmXButtonUp = 0x020C;
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
    private readonly Dictionary<string, Dictionary<string, MapInputBinding>> _pluginBindings =
        new(StringComparer.OrdinalIgnoreCase);
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
    public event EventHandler<PluginInputInvokedEventArgs>? PluginInputInvoked;

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
        RestartMonitoringIfNeeded();
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
            foreach (var binding in _pluginBindings.Values.SelectMany(bindings => bindings.Values))
                InitializePressedKey(binding);
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

        uint[] keys;
        lock (_keyboardStateLock)
        {
            if (!_keyboardBindingsActive
                || generation != _keyboardPollGeneration
                || _disposed)
            {
                return;
            }
            keys = new[]
            {
                _quickScan,
                _overlayToggle,
                _manualRecognition,
                _gameMapToggle,
                _controlPanelToggle,
                _switchFloor,
                _saveMapCache
            }
            .Concat(_pluginBindings.Values.SelectMany(bindings => bindings.Values))
            .Where(binding => binding.Kind == MapInputBindingKind.Keyboard)
            .Select(binding => binding.VirtualKey)
            .Where(key => key != 0)
            .Distinct()
            .ToArray();
        }

        foreach (var key in keys)
            HandleKeyboardState(key, IsKeyDown(key), generation);
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
        List<(string PluginId, string BindingKey, MapInputBinding Binding)>? pluginMatches = null;
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
                foreach (var (pluginId, bindings) in _pluginBindings)
                {
                    foreach (var (bindingKey, binding) in bindings)
                    {
                        if (binding.Kind == MapInputBindingKind.Keyboard
                            && binding.VirtualKey == key)
                        {
                            (pluginMatches ??= []).Add((pluginId, bindingKey, binding));
                        }
                    }
                }
                goto Dispatch;
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

            foreach (var (pluginId, bindings) in _pluginBindings)
            {
                foreach (var (bindingKey, binding) in bindings)
                {
                    if (binding.Kind == MapInputBindingKind.Keyboard
                        && binding.VirtualKey == key
                        && AreRequiredModifiersDown(binding.Modifiers))
                    {
                        (pluginMatches ??= []).Add((pluginId, bindingKey, binding));
                    }
                }
            }
        }

    Dispatch:
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
        if (pluginMatches is not null)
        {
            foreach (var match in pluginMatches)
            {
                var pluginEvent = new PluginInputInvokedEventArgs(
                    match.PluginId,
                    match.BindingKey,
                    invoked.Timestamp,
                    isDown);
                DispatchPluginInput(pluginEvent);
            }
        }
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
}
/*
 * 文件职责：MapGlobalInputService。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
