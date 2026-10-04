using System.ComponentModel;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;

namespace IDVBuff.Plugins.SceneQuickActions;

internal sealed partial class SceneQuickActionsRunner
{
    // ==================== 拖拽热键 ====================

    /// <summary>接入宿主的插件输入通道：热键的按键监听、注入过滤都由宿主负责。</summary>
    public void AttachHotkeys(IPluginInputService? input, string pluginId)
    {
        if (_hotkeyService is not null)
            _hotkeyService.BindingInvoked -= OnHotkeyInvoked;

        _hotkeyService = input;
        _pluginId = pluginId;
        if (_hotkeyService is not null)
            _hotkeyService.BindingInvoked += OnHotkeyInvoked;
    }

    private string _pluginId = "scene-quick-actions";

    private void OnHotkeyInvoked(object? sender, PluginInputEventArgs args)
    {
        if (!args.IsDown
            || !string.Equals(args.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var options = Volatile.Read(ref _options);
        var isBag = options.DropBagHotkey.IsConfigured
            && string.Equals(args.BindingKey, SceneQuickActionsOptions.DropBagHotkeyKey, StringComparison.Ordinal);
        var isHotbar = options.DropHotbarHotkey.IsConfigured
            && string.Equals(args.BindingKey, SceneQuickActionsOptions.DropHotbarHotkeyKey, StringComparison.Ordinal);
        if (!isBag && !isHotbar)
            return;

        // 防抖：按住不放时只认第一次
        var now = DateTime.UtcNow;
        if (now < _lastHotkeyAt.AddMilliseconds(HotkeyDebounceMilliseconds))
            return;
        _lastHotkeyAt = now;

        if (Interlocked.CompareExchange(ref _hotkeyBusy, 1, 0) != 0)
            return;

        var scheduled = false;
        try
        {
            if (!IsGameForeground())
            {
                LogThrottled("热键已按下，但当前前台窗口不是游戏，已忽略。");
                return;
            }

            var window = GetForegroundWindow();
            CancellationToken token;
            lock (_sync)
            {
                if (_cancellation is null)
                    return;
                token = _cancellation.Token;
            }

            // 暂停「拾取相关」：默认 30 秒，免得把刚丢出来的东西又捡回去。
            var silence = Math.Clamp(
                options.DropSilenceSeconds,
                SceneQuickActionsOptions.DropSilenceMinimum,
                SceneQuickActionsOptions.DropSilenceMaximum);
            _dropSilenceUntil = DateTime.UtcNow.AddSeconds(silence);

            string[] work;
            var hotbarSlot = 0;
            if (isBag)
            {
                work = BuildBagDropWork();
                _logger.Info(
                    $"热键「丢光背包」触发：先按 Tab 开背包，再依次拖 {work.Length} 格到屏幕中心；"
                    + $"「拾取相关」暂停 {silence} 秒。");
            }
            else
            {
                var cursor = Volatile.Read(ref _hotbarCursor);
                hotbarSlot = cursor % HotbarSlotCount;
                work = BuildHotbarDropWork(ref cursor);
                Volatile.Write(ref _hotbarCursor, cursor);
                _logger.Info(
                    $"热键「逐个丢道具栏」触发：{work[0]}（先切出鼠标）；"
                    + $"「拾取相关」暂停 {silence} 秒。");
            }

            _ = Task.Run(() =>
            {
                try
                {
                    RunDropWork(work, window, isBag, hotbarSlot, options, token);
                }
                finally
                {
                    Interlocked.Exchange(ref _hotkeyBusy, 0);
                }
            });
            scheduled = true;
        }
        finally
        {
            if (!scheduled)
                Interlocked.Exchange(ref _hotkeyBusy, 0);
        }
    }

    /// <summary>热键改动后重新注册（设置页改键位时调用）。</summary>
    public void RefreshHotkeyBindings() => RegisterHotkeys();

    /// <summary>把热键绑定注册到宿主（键位改动后要重新注册）。</summary>
    private void RegisterHotkeys()
    {
        if (_hotkeyService is null)
            return;

        var options = Volatile.Read(ref _options);
        _hotkeyService.SetBinding(_pluginId, SceneQuickActionsOptions.DropBagHotkeyKey, options.DropBagHotkey);
        _hotkeyService.SetBinding(_pluginId, SceneQuickActionsOptions.DropHotbarHotkeyKey, options.DropHotbarHotkey);
        _logger.Info(
            $"热键已注册：丢光背包 {options.DropBagHotkey.DisplayName}，"
            + $"逐个丢道具栏 {options.DropHotbarHotkey.DisplayName}。");
    }

    /// <summary>丢光背包：上排 3 格 + 中排 3 格，逐个拖到屏幕正中心松手。</summary>
    private static string[] BuildBagDropWork() =>
    [
        .. Enumerable.Range(1, BagSlotCount).Select(index => DescribeSlot("背包", index))
    ];

    /// <summary>逐个丢道具栏：每按一次取下一个格子（1 → 2 → 3 → 4 循环）。</summary>
    private static string[] BuildHotbarDropWork(ref int cursor)
    {
        var slot = cursor % HotbarSlotCount;
        cursor = (slot + 1) % HotbarSlotCount;
        return [DescribeSlot("道具栏", slot + 1)];
    }

    private static string DescribeSlot(string kind, int index) => $"{kind}第 {index} 格";

    /// <summary>后台执行：切出鼠标 → 逐个拖到屏幕中心 → 恢复。</summary>
    private void RunDropWork(string[] labels, IntPtr window, bool isBag, int hotbarSlot,
        SceneQuickActionsOptions options, CancellationToken token)
    {
        var wokeMouse = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!TryGetClientBounds(window, out var bounds))
                return;

            if (!SceneQuickActionsDropPlan.TryGetSlots(
                    bounds.Width, bounds.Height, isBag, hotbarSlot, out var source))
            {
                LogThrottled($"拖拽未执行：游戏客户区 {bounds.Width}×{bounds.Height} 不是 16:9 或 16:10。");
                return;
            }

            // 两个功能都少了这一步：不切出鼠标，游戏里根本没有可拖的指针。
            // 功能一要先开背包（格子才在），功能二只要指针切出来。
            WakeMouseForDrop(window, options, token, ref wokeMouse);

            var originX = bounds.X;
            var originY = bounds.Y;
            var targetX = originX + (int)Math.Round(DropTargetX * bounds.Width);
            var targetY = originY + (int)Math.Round(DropTargetY * bounds.Height);

            // 拖拽快慢由设置页的「拖拽速度」档位决定。
            var timing = SceneQuickActionsOptions.GetDropTiming(
                options.DropSpeedIndex);

            for (var index = 0; index < source.Length; index++)
            {
                if (!IsGameForeground())
                {
                    _logger.Warning("游戏已不在前台，放弃本次拖拽。");
                    return;
                }

                var fromX = originX + (int)Math.Round(source[index].X * bounds.Width);
                var fromY = originY + (int)Math.Round(source[index].Y * bounds.Height);
                DragOnce(window, fromX, fromY, targetX, targetY, timing, options,
                    index < source.Length - 1 ? timing.Interval : 0, token);
                _logger.Info($"已把 {labels[index]} 拖到屏幕中心（{fromX},{fromY} → {targetX},{targetY}）。");

            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.Warning($"拖拽失败：{exception.Message}");
        }
        finally
        {
            // 再把背包关回去，恢复游戏手势（再按一次 Tab 是开/关切换）。
            if (wokeMouse)
                CloseBackpack(window, options);
        }
    }

    /// <summary>拖拽前切出鼠标：按一次 Tab 打开背包，等指针真正出现。</summary>
    private void WakeMouseForDrop(IntPtr window, SceneQuickActionsOptions options,
        CancellationToken token, ref bool wokeMouse)
    {
        token.ThrowIfCancellationRequested();
        EnsureForeground(window);
        NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
        wokeMouse = true;
        _logger.Info("已按 Tab 打开背包切出鼠标（准备拖拽）。");

        var wait = Math.Max(options.WakeDelayMilliseconds, MinimumWakeDelayMilliseconds);
        SleepAfterStep(options, wait, token);
    }

    /// <summary>按一次左键拖拽：移动 → 按下 → 缓动到目标 → 松手。快慢由 timing 决定。</summary>
    private static void DragOnce(
        IntPtr window,
        int fromX,
        int fromY,
        int targetX,
        int targetY,
        (int Move, int Settle, int Interval) timing,
        SceneQuickActionsOptions options,
        int afterReleaseMilliseconds,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SetCursor(fromX, fromY);
        SleepAfterStep(options, timing.Settle, token);

        EnsureForeground(window);
        SendMouse(MouseeventfLeftdown);
        try
        {
            SleepAfterStep(options, timing.Settle, token);
            const int steps = 16;
            var perStep = Math.Max(1, timing.Move / steps);
            for (var step = 1; step <= steps; step++)
            {
                token.ThrowIfCancellationRequested();
                EnsureForeground(window);
                var progress = step / (double)steps;
                var eased = progress * progress * (3 - (2 * progress));
                SetCursor(
                    (int)Math.Round(fromX + ((targetX - fromX) * eased)),
                    (int)Math.Round(fromY + ((targetY - fromY) * eased)));
                NativeInput.Sleep(perStep, token);
            }
            SleepAfterStep(options, 0, token);
        }
        finally
        {
            SendMouse(MouseeventfLeftup);
        }
        SleepAfterStep(options, afterReleaseMilliseconds, token);
    }

    private static void SetCursor(int x, int y)
    {
        if (!SetCursorPos(x, y))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法移动鼠标指针。");
    }

    private static void SendMouse(uint flags)
    {
        var input = new NativeInputStructure
        {
            Type = InputMouse,
            Data = new NativeInputUnion
            {
                Mouse = new MouseInput
                {
                    Flags = flags,
                    ExtraInfo = InjectionMarker
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<NativeInputStructure>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注入鼠标输入。");
    }

    private static bool IsGameForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
            return false;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, "dwrg", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetClientBounds(IntPtr window, out ClientBounds bounds)
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

        bounds = new ClientBounds(origin.X, origin.Y, width, height);
        return true;
    }

    private readonly record struct ClientBounds(int X, int Y, int Width, int Height);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private const uint InputMouse = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInputStructure
    {
        public uint Type;
        public NativeInputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInputStructure[] inputs, int size);

}
