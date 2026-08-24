using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.AutoGatling;

/// <summary>
/// 自动加特林的 Win32 实现：T/Y 由低级键盘钩子拦截，实际操作由
/// SendInput 注入。开火和装弹始终串行，停止时保证释放鼠标左键。
/// </summary>
public sealed partial class AutoGatlingService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint LlkhfInjected = 0x00000010;
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private static readonly IntPtr InputInjectionMarker =
        new(InputInjectionMarkers.HostGeneratedInput);

    private readonly object _sync = new();
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly Action<string> _log;
    private readonly AutoGatlingOptions _options;
    private PluginInputBinding _inventoryBinding = new();
    private PluginInputBinding _activateBinding = new();
    private PluginInputBinding _reloadBinding = new();
    private IntPtr _keyboardHook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Thread? _operationThread;
    private CancellationTokenSource? _operationCancellation;
    private bool _activateKeyDown;
    private bool _reloadKeyDown;
    private bool _started;
    private bool _disposed;

    public AutoGatlingService(AutoGatlingOptions options, Action<string> log)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _keyboardProc = KeyboardHookCallback;
    }

    public bool IsStarted
    {
        get
        {
            lock (_sync)
                return _started;
        }
    }

    public void ConfigureBindings(
        PluginInputBinding inventoryBinding,
        PluginInputBinding activateBinding,
        PluginInputBinding reloadBinding)
    {
        ArgumentNullException.ThrowIfNull(inventoryBinding);
        ArgumentNullException.ThrowIfNull(activateBinding);
        ArgumentNullException.ThrowIfNull(reloadBinding);
        ValidateBinding(inventoryBinding, nameof(inventoryBinding));
        ValidateBinding(activateBinding, nameof(activateBinding));
        ValidateBinding(reloadBinding, nameof(reloadBinding));

        var restart = false;
        lock (_sync)
        {
            restart = _started;
        }
        if (restart)
            Stop();

        lock (_sync)
        {
            _inventoryBinding = inventoryBinding.Clone();
            _activateBinding = activateBinding.Clone();
            _reloadBinding = reloadBinding.Clone();
        }

        if (restart)
            Start();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_started)
                return;
            if (!AreBindingsReady())
                return;
            if (HasDuplicateBindings())
                throw new InvalidOperationException("自动加特林的三个按键不能重复。");
            _started = true;
        }

        try
        {
            StartHookThread();
        }
        catch
        {
            lock (_sync)
                _started = false;
            throw;
        }
    }

    public void Stop()
    {
        Thread? hookThread;
        Thread? operationThread;
        uint hookThreadId;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (!_started && _hookThread is null && _operationThread is null)
                return;

            _started = false;
            _activateKeyDown = false;
            _reloadKeyDown = false;
            cancellation = _operationCancellation;
            hookThread = _hookThread;
            operationThread = _operationThread;
            hookThreadId = _hookThreadId;
            _hookThread = null;
            _hookThreadId = 0;
        }

        cancellation?.Cancel();
        if (hookThreadId != 0)
            PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (hookThread is not null && hookThread != Thread.CurrentThread)
            hookThread.Join(TimeSpan.FromSeconds(2));
        if (operationThread is not null && operationThread != Thread.CurrentThread)
            operationThread.Join(TimeSpan.FromSeconds(2));

        SendLeftButton(down: false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
    }

    private void StartHookThread()
    {
        using var started = new ManualResetEventSlim();
        Exception? startupError = null;
        var thread = new Thread(() =>
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();
                PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                var module = GetModuleHandle(null);
                _keyboardHook = SetWindowsHookEx(
                    WhKeyboardLl,
                    _keyboardProc,
                    module,
                    0);
                if (_keyboardHook == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法注册自动加特林键盘钩子。");
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
                startupError = exception;
                started.Set();
            }
            finally
            {
                if (_keyboardHook != IntPtr.Zero)
                    UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                _hookThreadId = 0;
            }
        })
        {
            IsBackground = true,
            Name = "IDVB auto-gatling hook"
        };

        lock (_sync)
            _hookThread = thread;
        thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("自动加特林键盘钩子启动超时。");
        if (startupError is not null)
        {
            thread.Join();
            lock (_sync)
                _hookThread = null;
            throw startupError;
        }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((keyboard.Flags & LlkhfInjected) == 0)
            {
                var message = (uint)wParam.ToInt64();
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var isUp = message is WmKeyUp or WmSysKeyUp;
                if (isDown || isUp)
                {
                    if (HandleTrigger(
                        _activateBinding,
                        ref _activateKeyDown,
                        keyboard.VirtualKey,
                        isDown,
                        isUp,
                        activate: true))
                    {
                        return new IntPtr(1);
                    }

                    if (HandleTrigger(
                        _reloadBinding,
                        ref _reloadKeyDown,
                        keyboard.VirtualKey,
                        isDown,
                        isUp,
                        activate: false))
                    {
                        return new IntPtr(1);
                    }
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool HandleTrigger(
        PluginInputBinding binding,
        ref bool physicalKeyDown,
        uint virtualKey,
        bool isDown,
        bool isUp,
        bool activate)
    {
        if (!binding.IsConfigured || binding.VirtualKey != virtualKey)
            return false;

        if (isDown)
        {
            if (!AreRequiredModifiersDown(binding.Modifiers))
                return false;
            var firstDown = !physicalKeyDown;
            physicalKeyDown = true;
            if (firstDown)
                StartOperation(activate);
            return true;
        }

        if (isUp)
        {
            if (!physicalKeyDown)
                return false;
            physicalKeyDown = false;
            return true;
        }

        return false;
    }

    private void StartOperation(bool activate)
    {
        lock (_sync)
        {
            if (!_started || _operationThread is { IsAlive: true })
                return;

            var cancellation = new CancellationTokenSource();
            _operationCancellation = cancellation;
            _operationThread = new Thread(() => RunOperation(activate, cancellation))
            {
                IsBackground = true,
                Name = activate
                    ? "IDVB auto-gatling fire"
                    : "IDVB auto-gatling reload"
            };
            _operationThread.Start();
        }
    }

    private void RunOperation(bool activate, CancellationTokenSource cancellation)
    {
        try
        {
            if (activate)
            {
                ExecuteActivationAsync(cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                ExecuteReloadAsync(cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
            }
        }
        catch (OperationCanceledException)
        {
            _log("自动加特林操作已取消。");
        }
        catch (Exception exception)
        {
            _log($"自动加特林操作失败：{exception.Message}");
        }
        finally
        {
            SendLeftButton(down: false);
            lock (_sync)
            {
                if (ReferenceEquals(_operationCancellation, cancellation))
                {
                    _operationCancellation = null;
                    _operationThread = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task ExecuteActivationAsync(CancellationToken cancellationToken)
    {
        if (!TryGetLayout(out var layout))
        {
            _log("自动加特林未执行：当前游戏客户区不是精确的 16:9 或 16:10。");
            return;
        }

        var slots = AutoGatlingPlan.GetInventorySlotSequence(
            _options.EquipmentSlotCount);
        var cycleCount = Math.Clamp(
            _options.ActivationCycleCount,
            1,
            AutoGatlingOptions.MaximumActivationCycleCount);
        for (var cycle = 0; cycle < cycleCount; cycle++)
        {
            foreach (var slot in slots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ExecuteFireMethodAsync(layout, slot, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteReloadAsync(CancellationToken cancellationToken)
    {
        if (!TryGetLayout(out var layout))
        {
            _log("自动加特林未执行：当前游戏客户区不是精确的 16:9 或 16:10。");
            return;
        }

        var slots = AutoGatlingPlan.GetInventorySlotSequence(
            _options.EquipmentSlotCount);
        foreach (var slot in slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteReloadMethodAsync(layout, slot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteFireMethodAsync(
        GameLayout layout,
        int slot,
        CancellationToken cancellationToken)
    {
        await PressBindingAsync(_inventoryBinding, cancellationToken)
            .ConfigureAwait(false);
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);

        MoveTo(layout, AutoGatlingPlan.GetInventorySlot(layout.Coordinates, slot));
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);

        SendLeftButton(down: true);
        try
        {
            await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
            await MoveSmoothlyAsync(
                layout,
                AutoGatlingPlan.GetHotbarSlot(layout.Coordinates),
                _options.DragDelayMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendLeftButton(down: false);
        }

        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
        await PressBindingAsync(_inventoryBinding, cancellationToken)
            .ConfigureAwait(false);
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
        await ClickLeftAsync(cancellationToken).ConfigureAwait(false);
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteReloadMethodAsync(
        GameLayout layout,
        int slot,
        CancellationToken cancellationToken)
    {
        await PressBindingAsync(_inventoryBinding, cancellationToken)
            .ConfigureAwait(false);
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);

        MoveTo(layout, AutoGatlingPlan.GetInventorySlot(layout.Coordinates, slot));
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);

        SendLeftButton(down: true);
        try
        {
            await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
            await MoveSmoothlyAsync(
                layout,
                AutoGatlingPlan.GetHotbarSlot(layout.Coordinates),
                _options.DragDelayMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendLeftButton(down: false);
        }

        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
        await PressBindingAsync(_inventoryBinding, cancellationToken)
            .ConfigureAwait(false);
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
        await PressKeyAsync(AutoGatlingPlan.ReloadVirtualKey, cancellationToken)
            .ConfigureAwait(false);
        await JitteredDelayAsync(
            _options.ReloadDelayMilliseconds,
            cancellationToken).ConfigureAwait(false);
        await StandardDelayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ClickLeftAsync(CancellationToken cancellationToken)
    {
        SendLeftButton(down: true);
        try
        {
            await JitteredDelayAsync(
                _options.KeyPressDelayMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendLeftButton(down: false);
        }
    }

    private async Task PressKeyAsync(
        uint virtualKey,
        CancellationToken cancellationToken)
    {
        SendKey(virtualKey, keyUp: false);
        try
        {
            await JitteredDelayAsync(
                _options.KeyPressDelayMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SendKey(virtualKey, keyUp: true);
        }
    }

    private Task StandardDelayAsync(CancellationToken cancellationToken) =>
        JitteredDelayAsync(
            _options.StandardDelayMilliseconds,
            cancellationToken);

    private async Task PressBindingAsync(
        PluginInputBinding binding,
        CancellationToken cancellationToken)
    {
        var modifierKeys = GetModifierVirtualKeys(binding.Modifiers).ToArray();
        foreach (var modifierKey in modifierKeys)
            SendKey(modifierKey, keyUp: false);

        try
        {
            await PressKeyAsync(binding.VirtualKey, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            for (var index = modifierKeys.Length - 1; index >= 0; index--)
                SendKey(modifierKeys[index], keyUp: true);
        }
    }

    /// <summary>
    /// 在鼠标左键保持按下时，按多个中间点移动到目标位置。
    /// 拖动延迟是整段轨迹的总时长，而不是移动到终点后的空等。
    /// </summary>
    private async Task MoveSmoothlyAsync(
        GameLayout layout,
        PluginInventoryCoordinate coordinate,
        int baseDurationMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!GetCursorPos(out var start))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法读取当前鼠标位置。");
        }

        var target = ToScreenPoint(layout, coordinate);
        var distance = Math.Sqrt(
            Math.Pow(target.X - start.X, 2)
            + Math.Pow(target.Y - start.Y, 2));
        var stepCount = Math.Clamp((int)Math.Ceiling(distance / 24d), 4, 48);
        var (minimumRandomDelay, maximumRandomDelay) =
            _options.GetOrderedRandomDelayRange();
        var durationMilliseconds = _options.CoerceDelay(baseDurationMilliseconds)
            + Random.Shared.Next(minimumRandomDelay, maximumRandomDelay + 1);
        var stopwatch = Stopwatch.StartNew();

        for (var step = 1; step <= stepCount; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = step / (double)stepCount;
            // Smooth-step easing avoids an abrupt start/stop while preserving
            // a continuous path between the inventory slot and hotbar slot.
            var eased = progress * progress * (3d - (2d * progress));
            var x = (int)Math.Round(start.X + ((target.X - start.X) * eased));
            var y = (int)Math.Round(start.Y + ((target.Y - start.Y) * eased));
            if (!SetCursorPos(x, y))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法移动鼠标到拖动路径上的目标坐标。");
            }

            var targetElapsed = durationMilliseconds * progress;
            var remainingMilliseconds = targetElapsed - stopwatch.Elapsed.TotalMilliseconds;
            if (remainingMilliseconds > 0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(remainingMilliseconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private Task JitteredDelayAsync(
        int milliseconds,
        CancellationToken cancellationToken)
    {
        var (minimum, maximum) = _options.GetOrderedRandomDelayRange();
        return Task.Delay(
            _options.CoerceDelay(milliseconds)
            + Random.Shared.Next(minimum, maximum + 1),
            cancellationToken);
    }

    private static void MoveTo(
        GameLayout layout,
        PluginInventoryCoordinate coordinate)
    {
        var target = ToScreenPoint(layout, coordinate);
        if (!SetCursorPos(target.X, target.Y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标到目标坐标。");
    }

    private static NativePoint ToScreenPoint(
        GameLayout layout,
        PluginInventoryCoordinate coordinate)
    {
        var x = layout.OriginX + (int)Math.Round(coordinate.X * layout.Width);
        var y = layout.OriginY + (int)Math.Round(coordinate.Y * layout.Height);
        return new NativePoint { X = x, Y = y };
    }

    private static void SendKey(uint virtualKey, bool keyUp)
    {
        var input = new NativeInput
        {
            Type = InputKeyboard,
            Data = new NativeInputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    Flags = keyUp ? KeyeventfKeyup : 0,
                    ExtraInfo = InputInjectionMarker
                }
            }
        };
        lock (typeof(AutoGatlingService))
        {
            if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入键盘输入。");
        }
    }

    private static void SendLeftButton(bool down)
    {
        var input = new NativeInput
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = down ? MouseeventfLeftdown : MouseeventfLeftup,
                    ExtraInfo = InputInjectionMarker
                }
            }
        };
        lock (typeof(AutoGatlingService))
            _ = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
    }

    private bool TryGetLayout(out GameLayout layout)
    {
        layout = default;
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetClientRect(window, out var rect))
            return false;

        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin))
            return false;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (!AutoGatlingPlan.TryGetCoordinates(width, height, out var coordinates)
            || coordinates is null)
        {
            return false;
        }

        layout = new GameLayout(origin.X, origin.Y, width, height, coordinates);
        return true;
    }

    private bool AreBindingsReady() =>
        _inventoryBinding.IsConfigured
        && _activateBinding.IsConfigured
        && _reloadBinding.IsConfigured;

    private bool HasDuplicateBindings() =>
        _inventoryBinding.Equals(_activateBinding)
        || _inventoryBinding.Equals(_reloadBinding)
        || _activateBinding.Equals(_reloadBinding);

    private static IEnumerable<uint> GetModifierVirtualKeys(
        PluginInputModifiers modifiers)
    {
        if (modifiers.HasFlag(PluginInputModifiers.Control))
            yield return 0x11;
        if (modifiers.HasFlag(PluginInputModifiers.Alt))
            yield return 0x12;
        if (modifiers.HasFlag(PluginInputModifiers.Shift))
            yield return 0x10;
        if (modifiers.HasFlag(PluginInputModifiers.Windows))
            yield return 0x5B;
    }

    private static void ValidateBinding(PluginInputBinding binding, string parameterName)
    {
        if (binding.IsConfigured
            && (binding.Kind != PluginInputBindingKind.Keyboard
                || binding.VirtualKey == 0
                || binding.VirtualKey > ushort.MaxValue))
        {
            throw new ArgumentException("自动加特林只支持有效的键盘按键绑定。", parameterName);
        }
    }

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

    private readonly record struct GameLayout(
        int OriginX,
        int OriginY,
        int Width,
        int Height,
        IReadOnlyList<PluginInventoryCoordinate> Coordinates);

}
