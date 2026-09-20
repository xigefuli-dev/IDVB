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
    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
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
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint LlmhfInjected = 0x00000001;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfXup = 0x0100;
    // Must match MapGlobalInputService so auto-clicker input is not
    // reinterpreted as a host hotkey or mouse binding.
    private static readonly IntPtr InputInjectionMarker =
        new(InputInjectionMarkers.HostGeneratedInput);

    private readonly object _sync = new();
    private readonly object _sendInputSync = new();
    private readonly LowLevelMouseProc _mouseProc;
    private readonly AutoClickerOptions _options;
    private PluginInputBinding _triggerBinding =
        PluginInputBinding.Mouse(PluginMouseButton.Right);
    private ushort _outputVirtualKey = 0x46;
    private bool _outputVirtualKeyConfigured = true;
    private IntPtr _hook;
    private IntPtr _keyboardHook;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private Thread? _clickingThread;
    private bool _physicalButtonDown;
    private long _physicalButtonDownAt;
    private bool _clicking;
    private bool _outputSessionActive;
    private int _pressGeneration;
    private bool _started;

    public AutoClickerService(AutoClickerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    private readonly LowLevelKeyboardProc _keyboardProc;

    private static long MillisecondsToTicks(int milliseconds, double tickRate) =>
        (long)(milliseconds * tickRate / 1000.0);

    public void ConfigureBindings(
        PluginInputBinding triggerBinding,
        PluginInputBinding outputBinding)
    {
        ArgumentNullException.ThrowIfNull(triggerBinding);
        ArgumentNullException.ThrowIfNull(outputBinding);
        if (outputBinding.IsConfigured
            && (outputBinding.Kind != PluginInputBindingKind.Keyboard
                || outputBinding.VirtualKey == 0))
        {
            throw new ArgumentException("连点器按键绑定无效。", nameof(triggerBinding));
        }

        var nextTriggerBinding = triggerBinding.Clone();
        var nextOutputVirtualKey = outputBinding.IsConfigured
            ? checked((ushort)outputBinding.VirtualKey)
            : (ushort)0;
        bool restart;
        lock (_sync)
            restart = _started;

        // Stop while the old output key is still configured. Otherwise
        // Stop() would release the newly selected key and leave an old
        // injected key-down stuck in the target application.
        if (restart)
            Stop();

        lock (_sync)
        {
            _triggerBinding = nextTriggerBinding;
            _outputVirtualKeyConfigured = outputBinding.IsConfigured;
            if (outputBinding.IsConfigured)
                _outputVirtualKey = nextOutputVirtualKey;
        }

        if (restart)
            Start();
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
                return;
            if (!_triggerBinding.IsConfigured || !_outputVirtualKeyConfigured)
                return;
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
        Thread? clickingThread;
        uint hookThreadId;
        bool releaseOutputs;
        lock (_sync)
        {
            if (!_started)
                return;
            _started = false;
            _physicalButtonDown = false;
            _pressGeneration++;
            releaseOutputs = _outputSessionActive || _clicking;
            _outputSessionActive = false;
            _clicking = false;
            Monitor.PulseAll(_sync);
            hookThread = _hookThread;
            hookThreadId = _hookThreadId;
            clickingThread = _clickingThread;
            _hookThread = null;
            _hookThreadId = 0;
        }
        if (releaseOutputs)
            SendReleaseSignals();
        if (hookThread is not null)
        {
            if (hookThreadId != 0)
                PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (hookThread != Thread.CurrentThread)
                hookThread.Join(TimeSpan.FromSeconds(2));
        }
        if (clickingThread is not null && clickingThread != Thread.CurrentThread)
            clickingThread.Join(TimeSpan.FromSeconds(2));
    }

    public void Dispose() => Stop();

    private void StartHookThread()
    {
        using var started = new ManualResetEventSlim();
        Exception? startupError = null;
        var thread = new Thread(() =>
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();
                // 先强制创建本线程的消息队列，保证立即 PostThreadMessage(WM_QUIT) 能送达。
                PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                var module = GetModuleHandle(null);
                _hook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
                _keyboardHook = SetWindowsHookEx(
                    WhKeyboardLl,
                    _keyboardProc,
                    module,
                    0);
                if (_hook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法注册连点器全局输入钩子。");
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
                if (_hook != IntPtr.Zero)
                    UnhookWindowsHookEx(_hook);
                if (_keyboardHook != IntPtr.Zero)
                    UnhookWindowsHookEx(_keyboardHook);
                _hook = IntPtr.Zero;
                _keyboardHook = IntPtr.Zero;
                _hookThreadId = 0;
            }
        })
        {
            IsBackground = true,
            Name = "IDVB auto-clicker hooks"
        };

        lock (_sync)
            _hookThread = thread;
        thread.Start();
        if (!started.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("连点器钩子线程启动超时。");
        if (startupError is not null)
        {
            thread.Join();
            lock (_sync)
                _hookThread = null;
            throw startupError;
        }
    }

    private IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            // 注入的连点信号（本服务 SendInput 产生）：透传给目标程序。
            if ((mouse.Flags & LlmhfInjected) == 0
                && TryGetMouseButton(
                    (uint)wParam.ToInt64(),
                    lParam,
                    out var button,
                    out var isDown))
            {
                PluginInputBinding trigger;
                lock (_sync)
                    trigger = _triggerBinding;
                if (trigger.Kind == PluginInputBindingKind.Mouse
                    && trigger.MouseButton == button)
                {
                    var swallow = isDown
                        ? HandlePhysicalButtonDown()
                        : HandlePhysicalButtonUp();
                    if (swallow)
                        return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var keyboard = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((keyboard.Flags & 0x00000010) == 0)
            {
                var message = (uint)wParam.ToInt64();
                var isDown = message is WmKeyDown or WmSysKeyDown;
                if (isDown || message is WmKeyUp or WmSysKeyUp)
                {
                    PluginInputBinding trigger;
                    lock (_sync)
                        trigger = _triggerBinding;
                    if (trigger.Kind == PluginInputBindingKind.Keyboard
                        && trigger.VirtualKey == keyboard.VirtualKey
                        && (isDown
                            ? AreRequiredModifiersDown(trigger.Modifiers)
                            : true))
                    {
                        var swallow = isDown
                            ? HandlePhysicalButtonDown()
                            : HandlePhysicalButtonUp();
                        if (swallow)
                            return new IntPtr(1);
                    }
                }
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    /// <summary>物理鼠标右键按下；返回 true 表示已接管、应吞掉该输入。</summary>
    private bool HandlePhysicalButtonDown()
    {
        Thread? toStart = null;
        bool swallow;
        lock (_sync)
        {
            if (!_started)
                return false;
            if (_physicalButtonDown)
                return _clicking; // 防御重复按下：已接管则吞，未接管则透传
            _physicalButtonDown = true;
            _physicalButtonDownAt = Stopwatch.GetTimestamp();
            _pressGeneration++;
            if (_clickingThread is not { IsAlive: true })
            {
                _clickingThread = new Thread(ClickingLoop)
                {
                    IsBackground = true,
                    Name = "IDVB auto-clicker loop"
                };
                toStart = _clickingThread;
            }
            swallow = _clicking;
        }
        toStart?.Start();
        return swallow;
    }

    /// <summary>物理鼠标右键抬起；返回 true 表示已接管、应吞掉该输入。</summary>
    private bool HandlePhysicalButtonUp()
    {
        bool releaseOutputs;
        lock (_sync)
        {
            _physicalButtonDown = false;
            releaseOutputs = _outputSessionActive || _clicking;
            _outputSessionActive = false;
            Monitor.PulseAll(_sync);
        }
        // Always pass the real right-button up through to the target. The
        // handoff sends a synthetic right-up when clicking begins, but some
        // games only release mouse capture on the physical up message. If we
        // swallow it here, a later control window can remain visually active
        // while left-clicks are still routed to the game.
        if (releaseOutputs)
            SendReleaseSignals();
        return false;
    }

    private void ClickingLoop()
    {
        var tickRate = Stopwatch.Frequency;
        var sessionGeneration = -1;
        timeBeginPeriod(1);
        try
        {
            long nextClickAt = 0;
            while (true)
            {
                bool down;
                long downAt;
                bool clicking;
                int generation;
                lock (_sync)
                {
                    if (!_started)
                        break;
                    down = _physicalButtonDown;
                    downAt = _physicalButtonDownAt;
                    generation = _pressGeneration;
                    if (sessionGeneration != generation)
                    {
                        // A quick release/re-press can reuse this worker. The
                        // new press must re-arm the right-button handoff.
                        sessionGeneration = generation;
                        _clicking = false;
                        _outputSessionActive = false;
                        nextClickAt = 0;
                    }
                    clicking = _clicking;
                }
                if (!down)
                {
                    lock (_sync)
                    {
                        // Make a fast re-press able to create the next worker
                        // while this one is still running its finally block.
                        if (ReferenceEquals(_clickingThread, Thread.CurrentThread))
                            _clickingThread = null;
                    }
                    break;
                }

                var heldMs = (Stopwatch.GetTimestamp() - downAt) * 1000.0 / tickRate;
                if (!clicking)
                {
                    if (heldMs < AutoClickerPolicy.HoldBeforeClickMilliseconds)
                    {
                        Thread.Sleep(5); // 尚未达到长按阈值，短等后复查
                        continue;
                    }
                    // 达到阈值，先结束此前透传的物理右键按下，再进入接管态。
                    // 在 SendInput 成功前不吞掉物理右键抬起，否则注入失败会
                    // 把目标程序永久留在“右键按下”状态。
                    if (!SendTriggerUp())
                        break;
                    lock (_sync)
                    {
                        if (!_started
                            || !_physicalButtonDown
                            || _pressGeneration != sessionGeneration)
                        {
                            continue;
                        }
                        _clicking = true;
                        _outputSessionActive = true;
                    }
                    nextClickAt = Stopwatch.GetTimestamp();
                    continue;
                }

                // 每轮读取 volatile options，让设置页改动即时生效。F↓ 先发，
                // 保持按下后延迟再 F↑，余量（抬手后延迟）在等待下一次周期时自然消耗。
                var keyDownRandomDelay = _options.NextRandomDelayMilliseconds();
                var upRandomDelay = _options.NextRandomDelayMilliseconds();
                var keyDownTicks = _options.KeyDownTicks(tickRate)
                    + MillisecondsToTicks(keyDownRandomDelay, tickRate);
                var periodTicks = _options.PeriodTicks(tickRate)
                    + MillisecondsToTicks(keyDownRandomDelay + upRandomDelay, tickRate);
                if (!SendKeyDown(sessionGeneration))
                    break;
                var downSentAt = Stopwatch.GetTimestamp();
                WaitUntil(downSentAt + keyDownTicks);
                if (!SendKeyUp(sessionGeneration))
                    break;
                nextClickAt += periodTicks;
                var now = Stopwatch.GetTimestamp();
                if (nextClickAt <= now)
                {
                    // Do not catch up missed ticks. A delayed SendInput or a
                    // scheduler pause must never turn into an input burst.
                    nextClickAt = now + periodTicks;
                }
                WaitUntil(nextClickAt);
            }
        }
        finally
        {
            bool releaseOutputs;
            lock (_sync)
            {
                releaseOutputs = _outputSessionActive || _clicking;
                _outputSessionActive = false;
                // 无条件清 _clicking：worker 退出即不再接管。若按 generation 匹配才清，
                // hold 窗口内快松开+重按（_pressGeneration 已递增）会让 _clicking 残留
                // true——重按不再连点（无 worker 续跑），下一次物理按下还被 swallow 误吞。
                // releaseOutputs 已在此前计算，F↑ 兜底不受影响。
                _clicking = false;
                if (ReferenceEquals(_clickingThread, Thread.CurrentThread))
                    _clickingThread = null;
                Monitor.PulseAll(_sync);
            }
            if (releaseOutputs)
                SendReleaseSignals();
            timeEndPeriod(1);
        }
    }
}
