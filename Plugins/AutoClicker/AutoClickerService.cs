using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.AutoClicker;

/// <summary>
/// 连点器的 Win32 实现：物理鼠标右键在短按时正常透传（不影响日常右键菜单等）；
/// 一旦按住达到长按阈值，钩子便接管——吞掉物理鼠标右键，
/// 用 SendInput 原子注入 F 按下/抬起；
/// 松开即停止，结束路径强制补发 F 抬起。
/// </summary>
public sealed class AutoClickerService : IDisposable
{
    private const int WhMouseLl = 14;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint LlmhfInjected = 0x00000001;
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint MouseeventfRightup = 0x0008;
    // Must match MapGlobalInputService so auto-clicker input is not
    // reinterpreted as a host hotkey or mouse binding.
    private static readonly IntPtr InputInjectionMarker =
        new(InputInjectionMarkers.HostGeneratedInput);

    private readonly object _sync = new();
    private readonly object _sendInputSync = new();
    private readonly LowLevelMouseProc _mouseProc;
    private readonly AutoClickerOptions _options;
    private IntPtr _hook;
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
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
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
                if (_hook == IntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "无法注册连点器全局鼠标钩子。");
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
                _hook = IntPtr.Zero;
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
        if (code >= 0 && AutoClickerPolicy.IsTriggerMouseMessage((uint)wParam.ToInt64()))
        {
            var mouse = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            // 注入的连点信号（本服务 SendInput 产生）：透传给目标程序。
            if ((mouse.Flags & LlmhfInjected) == 0)
            {
                var message = (uint)wParam.ToInt64();
                if (message == WmRButtonDown)
                {
                    if (HandlePhysicalButtonDown())
                        return new IntPtr(1);
                }
                else if (message == WmRButtonUp)
                {
                    if (HandlePhysicalButtonUp())
                        return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
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
                    if (!SendButtonUp())
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
                var keyDownTicks = _options.KeyDownTicks(tickRate);
                var periodTicks = _options.PeriodTicks(tickRate);
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

    /// <summary>接管时结束此前透传的物理鼠标右键按下。</summary>
    private bool SendButtonUp()
    {
        var input = new NativeInput
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = MouseeventfRightup,
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
                        VirtualKey = AutoClickerPolicy.OutputVirtualKey,
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
                        VirtualKey = AutoClickerPolicy.OutputVirtualKey,
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

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    private static extern IntPtr SetWindowsHookEx(
        int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
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
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
