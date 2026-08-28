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

}
