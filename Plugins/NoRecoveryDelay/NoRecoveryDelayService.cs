using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.NoRecoveryDelay;

public sealed class NoRecoveryDelayService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012, PmNoRemove = 0, LlkhfInjected = 0x10;
    private const uint InputMouse = 0, InputKeyboard = 1, KeyeventfKeyup = 2;
    private const uint MouseeventfLeftdown = 2, MouseeventfLeftup = 4;
    private static readonly IntPtr InjectionMarker = new(InputInjectionMarkers.HostGeneratedInput);
    private readonly object _sync = new();
    private readonly NoRecoveryDelayOptions _options;
    private readonly Action<string> _log;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private PluginInputBinding _inventoryBinding = new(), _activateBinding = new();
    private IntPtr _keyboardHook;
    private Thread? _hookThread, _operationThread;
    private uint _hookThreadId;
    private CancellationTokenSource? _stopCancellation;
    private volatile bool _activateKeyDown;
    private bool _started, _disposed;

    public NoRecoveryDelayService(NoRecoveryDelayOptions options, Action<string> log)
    { _options = options; _log = log; _keyboardProc = KeyboardHookCallback; }

    public void ConfigureBindings(PluginInputBinding inventory, PluginInputBinding activate)
    {
        ValidateBinding(inventory, nameof(inventory)); ValidateBinding(activate, nameof(activate));
        var restart = _started;
        if (restart) Stop();
        _inventoryBinding = inventory.Clone(); _activateBinding = activate.Clone();
        if (restart) Start();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_started) return;
            if (!_inventoryBinding.IsConfigured || !_activateBinding.IsConfigured) return;
            if (_inventoryBinding.Equals(_activateBinding)) throw new InvalidOperationException("背包按键与激活无后摇按键不能重复。");
            _started = true;
        }
        try { StartHookThread(); }
        catch { lock (_sync) _started = false; throw; }
    }

    public void Stop()
    {
        Thread? hook, operation; uint id; CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _started = false; _activateKeyDown = false;
            hook = _hookThread; operation = _operationThread; id = _hookThreadId;
            cancellation = _stopCancellation; _hookThread = null; _hookThreadId = 0;
        }
        cancellation?.Cancel();
        if (id != 0) PostThreadMessage(id, WmQuit, IntPtr.Zero, IntPtr.Zero);
        if (hook is not null && hook != Thread.CurrentThread) hook.Join(TimeSpan.FromSeconds(2));
        if (operation is not null && operation != Thread.CurrentThread) operation.Join(TimeSpan.FromSeconds(2));
        SendLeftButton(false);
    }

    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; }

    private void StartHookThread()
    {
        using var ready = new ManualResetEventSlim(); Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                _hookThreadId = GetCurrentThreadId(); PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
                _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
                if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册无后摇信仰键盘钩子。");
                ready.Set();
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
            catch (Exception ex) { error = ex; ready.Set(); }
            finally { if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; _hookThreadId = 0; }
        }) { IsBackground = true, Name = "IDVB no-recovery-delay hook" };
        lock (_sync) _hookThread = thread;
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("无后摇信仰键盘钩子启动超时。");
        if (error is not null) { thread.Join(); throw error; }
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((data.Flags & LlkhfInjected) == 0 && data.VirtualKey == _activateBinding.VirtualKey)
            {
                var message = (uint)wParam.ToInt64();
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    if (!RequiredModifiersDown(_activateBinding.Modifiers)) return CallNextHookEx(_keyboardHook, code, wParam, lParam);
                    if (!_activateKeyDown) { _activateKeyDown = true; StartOperation(); }
                    return new IntPtr(1);
                }
                if (message is WmKeyUp or WmSysKeyUp)
                {
                    _activateKeyDown = false;
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void StartOperation()
    {
        lock (_sync)
        {
            if (!_started || _operationThread is { IsAlive: true }) return;
            var cancellation = new CancellationTokenSource(); _stopCancellation = cancellation;
            _operationThread = new Thread(() => RunOperation(cancellation)) { IsBackground = true, Name = "IDVB no-recovery-delay operation" };
            _operationThread.Start();
        }
    }

    private void RunOperation(CancellationTokenSource cancellation)
    {
        try { ExecuteAsync(cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { _log("无后摇信仰操作已停止。"); }
        catch (Exception ex) { _log($"无后摇信仰操作失败：{ex.Message}"); }
        finally
        {
            SendLeftButton(false);
            lock (_sync) { if (ReferenceEquals(_stopCancellation, cancellation)) { _stopCancellation = null; _operationThread = null; } }
            cancellation.Dispose();
        }
    }

    private async Task ExecuteAsync(CancellationToken token)
    {
        if (!TryGetLayout(out var layout)) { _log("无后摇信仰未执行：当前游戏客户区不是精确的 16:9 或 16:10。"); return; }
        var slots = new[] { _options.InventorySlot1, _options.InventorySlot2 };
        var limit = _options.LoopMode == NoRecoveryDelayLoopMode.Rounds ? Math.Clamp(_options.LoopCount, 1, 20) : int.MaxValue;
        await PressBindingAsync(_inventoryBinding, token).ConfigureAwait(false); // 初始化
        await StandardDelayAsync(token).ConfigureAwait(false);
        for (var round = 0; round < limit; round++)
        {
            await ExecuteCycleAsync(layout, slots[round % 2], token).ConfigureAwait(false);
            if (round + 1 >= limit || (_options.LoopMode == NoRecoveryDelayLoopMode.Hold && !_activateKeyDown)) break;
            await PressBindingAsync(_inventoryBinding, token).ConfigureAwait(false); // 下一次初始化
            await StandardDelayAsync(token).ConfigureAwait(false);
        }
    }

    private async Task ExecuteCycleAsync(GameLayout layout, int inventorySlot, CancellationToken token)
    {
        MoveTo(layout, NoRecoveryDelayPlan.GetInventorySlot(layout.Coordinates, inventorySlot));
        await StandardDelayAsync(token).ConfigureAwait(false);
        SendLeftButton(true);
        try
        {
            await StandardDelayAsync(token).ConfigureAwait(false);
            await MoveSmoothlyAsync(layout, NoRecoveryDelayPlan.GetEquipmentSlot(layout.Coordinates, _options.EquipmentSlot), token).ConfigureAwait(false);
        }
        finally { SendLeftButton(false); }
        await StandardDelayAsync(token).ConfigureAwait(false);
        await PressBindingAsync(_inventoryBinding, token).ConfigureAwait(false);
        await StandardDelayAsync(token).ConfigureAwait(false);
        await ClickLeftAsync(token).ConfigureAwait(false);
        await StandardDelayAsync(token).ConfigureAwait(false);
    }

    private async Task MoveSmoothlyAsync(GameLayout layout, PluginInventoryCoordinate coordinate, CancellationToken token)
    {
        if (!GetCursorPos(out var start)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前鼠标位置。");
        var target = ToPoint(layout, coordinate);
        var distance = Math.Sqrt(Math.Pow(target.X - start.X, 2) + Math.Pow(target.Y - start.Y, 2));
        var steps = Math.Clamp((int)Math.Ceiling(distance / 24d), 4, 48);
        var (minimum, maximum) = _options.GetRandomRange();
        var duration = Math.Clamp(_options.DragDelayMilliseconds, 25, 10000) + Random.Shared.Next(minimum, maximum + 1);
        var watch = Stopwatch.StartNew();
        for (var step = 1; step <= steps; step++)
        {
            token.ThrowIfCancellationRequested(); var progress = step / (double)steps; var eased = progress * progress * (3 - 2 * progress);
            if (!SetCursorPos((int)Math.Round(start.X + (target.X - start.X) * eased), (int)Math.Round(start.Y + (target.Y - start.Y) * eased)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标。");
            var remaining = duration * progress - watch.Elapsed.TotalMilliseconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
        }
    }

    private async Task ClickLeftAsync(CancellationToken token)
    { SendLeftButton(true); try { await DelayAsync(_options.KeyPressDelayMilliseconds, token).ConfigureAwait(false); } finally { SendLeftButton(false); } }
    private async Task PressBindingAsync(PluginInputBinding binding, CancellationToken token)
    {
        var modifiers = ModifierKeys(binding.Modifiers).ToArray(); foreach (var key in modifiers) SendKey(key, false);
        try { SendKey(binding.VirtualKey, false); try { await DelayAsync(_options.KeyPressDelayMilliseconds, token).ConfigureAwait(false); } finally { SendKey(binding.VirtualKey, true); } }
        finally { for (var i = modifiers.Length - 1; i >= 0; i--) SendKey(modifiers[i], true); }
    }
    private Task StandardDelayAsync(CancellationToken token) => DelayAsync(Math.Max(1, _options.StandardDelayMilliseconds), token);
    private Task DelayAsync(int milliseconds, CancellationToken token)
    { var (min, max) = _options.GetRandomRange(); return Task.Delay(_options.CoerceDelay(milliseconds) + Random.Shared.Next(min, max + 1), token); }

    private bool TryGetLayout(out GameLayout layout)
    {
        layout = default; var window = GetForegroundWindow();
        if (window == IntPtr.Zero || !GetClientRect(window, out var rect)) return false;
        var origin = new NativePoint(); if (!ClientToScreen(window, ref origin)) return false;
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        if (!NoRecoveryDelayPlan.TryGetCoordinates(width, height, out var coordinates) || coordinates is null) return false;
        layout = new(origin.X, origin.Y, width, height, coordinates); return true;
    }
    private static NativePoint ToPoint(GameLayout layout, PluginInventoryCoordinate coordinate) =>
        new() { X = layout.X + (int)Math.Round(coordinate.X * layout.Width), Y = layout.Y + (int)Math.Round(coordinate.Y * layout.Height) };
    private static void MoveTo(GameLayout layout, PluginInventoryCoordinate coordinate)
    { var point = ToPoint(layout, coordinate); if (!SetCursorPos(point.X, point.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标到目标坐标。"); }
    private static void SendKey(uint key, bool up)
    {
        var input = new NativeInput { Type = InputKeyboard, Data = new NativeInputUnion { Keyboard = new KeyboardInput { VirtualKey = (ushort)key, Flags = up ? KeyeventfKeyup : 0, ExtraInfo = InjectionMarker } } };
        lock (typeof(NoRecoveryDelayService)) if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入键盘输入。");
    }
    private static void SendLeftButton(bool down)
    {
        var input = new NativeInput { Type = InputMouse, Data = new NativeInputUnion { Mouse = new MouseInput { Flags = down ? MouseeventfLeftdown : MouseeventfLeftup, ExtraInfo = InjectionMarker } } };
        lock (typeof(NoRecoveryDelayService)) _ = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
    }
    private static void ValidateBinding(PluginInputBinding binding, string name)
    { if (binding.IsConfigured && (binding.Kind != PluginInputBindingKind.Keyboard || binding.VirtualKey == 0 || binding.VirtualKey > ushort.MaxValue)) throw new ArgumentException("无后摇信仰只支持有效的键盘按键绑定。", name); }
    private static IEnumerable<uint> ModifierKeys(PluginInputModifiers modifiers)
    { if (modifiers.HasFlag(PluginInputModifiers.Control)) yield return 0x11; if (modifiers.HasFlag(PluginInputModifiers.Alt)) yield return 0x12; if (modifiers.HasFlag(PluginInputModifiers.Shift)) yield return 0x10; if (modifiers.HasFlag(PluginInputModifiers.Windows)) yield return 0x5B; }
    private static bool RequiredModifiersDown(PluginInputModifiers modifiers) =>
        (!modifiers.HasFlag(PluginInputModifiers.Control) || AnyDown(0x11, 0xA2, 0xA3)) && (!modifiers.HasFlag(PluginInputModifiers.Alt) || AnyDown(0x12, 0xA4, 0xA5)) &&
        (!modifiers.HasFlag(PluginInputModifiers.Shift) || AnyDown(0x10, 0xA0, 0xA1)) && (!modifiers.HasFlag(PluginInputModifiers.Windows) || AnyDown(0x5B, 0x5C));
    private static bool AnyDown(params int[] keys) => keys.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private readonly record struct GameLayout(int X, int Y, int Width, int Height, IReadOnlyList<PluginInventoryCoordinate> Coordinates);

    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VirtualKey, ScanCode, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage { public IntPtr Window; public uint Message; public IntPtr WParam, LParam; public uint Time; public NativePoint Point; public uint Private; }
    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostThreadMessage(uint id, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
}
