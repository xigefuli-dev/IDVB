using System.ComponentModel;
using System.Runtime.InteropServices;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.SceneQuickActions;

/// <summary>
/// 背包辅助的工作线程：按检测间隔抓一帧 → 模板匹配 → 命中则执行点击流程。
///
/// 线程只在插件启用期间存在；未在游戏中（dwrg.exe 不是前台窗口）时抓帧会立刻失败，
/// 开销接近 0。
///
/// 另外负责两个手动热键（默认 F5 / F6）：
///   F5 —— 把背包上排 3 格 + 中排 3 格的物品依次拖到屏幕正中心松手；
///   F6 —— 每按一次，把底部道具栏的下一个格子拖到屏幕正中心松手（1 → 2 → 3 → 4 循环）。
/// 触发后一段时间内暂停「拾取相关」，免得把刚丢出来的东西又捡回去。
/// </summary>
internal sealed class SceneQuickActionsRunner : IDisposable
{
    private const int MoveDurationMilliseconds = 140;
    private const int ClickHoldMilliseconds = 35;
    private const int KeyHoldMilliseconds = 30;
    private const int WakeHoldMilliseconds = 25;
    // 「切出鼠标后等待」的内部下限：按完 Tab 背包还没开出来就开始点会白点一次。
    private const int MinimumWakeDelayMilliseconds = 120;
    private const int PreClickDelayMilliseconds = 40;
    private const int QuickReplaceAttempts = 3;
    private const int QuickReplaceRetryMilliseconds = 150;
    private const int WarningThrottleSeconds = 20;

    // —— 拖拽热键相关 ——
    private const int HotkeyDebounceMilliseconds = 400;

    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;

    /// <summary>背包上排 3 格 + 中排 3 格的归一化坐标（实测于 1920×1080 探索模式背包界面）。</summary>
    private static readonly (double X, double Y)[] BagSlots =
    [
        (0.235, 0.240), (0.310, 0.240), (0.385, 0.240),
        (0.235, 0.390), (0.310, 0.390), (0.385, 0.390)
    ];

    /// <summary>底部道具栏 4 格的归一化坐标（与 PluginInventoryScale 的 Shape 3 实测吻合）。</summary>
    private static readonly (double X, double Y)[] HotbarSlots =
    [
        (0.39, 0.92), (0.47, 0.92), (0.56, 0.92), (0.64, 0.92)
    ];

    /// <summary>落点：屏幕正中心。</summary>
    private const double DropTargetX = 0.50;
    private const double DropTargetY = 0.50;

    private static readonly IntPtr InjectionMarker =
        new(InputInjectionMarkers.HostGeneratedInput);

    private readonly object _sync = new();
    private readonly GameFrameGrabber _grabber;
    private readonly SceneQuickActionsMatcher _matcher;
    private readonly IPluginLogger _logger;

    private SceneQuickActionsOptions _options = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _worker;
    private volatile bool _mapOpen;
    private bool _inviteArmed = true;
    private bool _pickupArmed = true;
    private DateTime _inviteReadyAt = DateTime.MinValue;
    private DateTime _pickupReadyAt = DateTime.MinValue;
    private string _lastWarning = string.Empty;
    private DateTime _lastWarningAt = DateTime.MinValue;
    private bool _disposed;

    // 热键状态（走宿主的 IPluginInputService，键盘键与鼠标键都能绑）
    private IPluginInputService? _hotkeyService;
    private int _hotkeyBusy;
    private int _hotbarCursor;
    private DateTime _lastHotkeyAt = DateTime.MinValue;
    private DateTime _dropSilenceUntil = DateTime.MinValue;

    public SceneQuickActionsRunner(
        GameFrameGrabber grabber,
        SceneQuickActionsMatcher matcher,
        IPluginLogger logger)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void SetOptions(SceneQuickActionsOptions options) =>
        Volatile.Write(ref _options, options ?? new SceneQuickActionsOptions());

    /// <summary>宿主地图界面打开时暂停检测：那时不可能出现这两个场景，且避免与识别流程抢抓帧。</summary>
    public void SetMapOpen(bool mapOpen) => _mapOpen = mapOpen;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_worker is { IsAlive: true })
                return;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "IDVB scene-quick-actions"
            };
            _worker.Start();
        }

        _logger.Info($"背包辅助已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
        RegisterHotkeys();
    }

    public void Stop()
    {
        if (_hotkeyService is not null)
        {
            _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
            _hotkeyService = null;
        }

        Thread? worker;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            worker = _worker;
            cancellation = _cancellation;
            _worker = null;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join(TimeSpan.FromSeconds(3));
        cancellation?.Dispose();

        _inviteArmed = true;
        _pickupArmed = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var options = Volatile.Read(ref _options);
            try
            {
                if (!_mapOpen && (options.InviteEnabled || options.PickupEnabled))
                    ProcessOnce(options, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                LogThrottled($"检测异常：{exception.Message}");
            }

            var interval = Math.Clamp(options.PollIntervalMilliseconds, 100, 5000);
            if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(interval)))
                break;
        }
    }

    private void ProcessOnce(SceneQuickActionsOptions options, CancellationToken token)
    {
        if (!_grabber.TryGrab(out var frame, out var failure, token) || frame is null)
        {
            if (!IsIdleFailure(failure))
                LogThrottled($"抓帧失败：{failure}");
            return;
        }

        using (frame)
        {
            var now = DateTime.UtcNow;
            if (options.InviteEnabled && ProcessInviteScene(frame, options, now, token))
                return;
            if (options.PickupEnabled && now >= _dropSilenceUntil)
                ProcessPickupScene(frame, options, now, token);
        }
    }

    private bool ProcessInviteScene(
        GrabbedFrame frame,
        SceneQuickActionsOptions options,
        DateTime now,
        CancellationToken token)
    {
        var matched = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.AcceptButton,
            options.MatchThreshold,
            out var acceptHit);
        if (!matched)
        {
            if (!_inviteArmed)
                _inviteArmed = true;
            return false;
        }

        if (!_inviteArmed || now < _inviteReadyAt)
            return false;

        _inviteArmed = false;
        _inviteReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
        PerformInvite(frame, acceptHit, options, token);
        return true;
    }

    private void PerformInvite(
        GrabbedFrame frame,
        MatchHit hit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        var (x, y) = SceneQuickActionsMatcher.ToScreen(hit.CenterX, hit.CenterY, frame.Bounds, frame.Size);
        _logger.Info($"识别到「接受」按钮（相似度 {hit.Score:P0}），执行「按 Tab 开背包切出鼠标 → 点击接受 → 按 Tab 关背包」。");

        WakeMouse(options, frame.WindowHandle, token);
        try
        {
            EnsureForeground(frame.WindowHandle);
            NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
            NativeInput.Sleep(PreClickDelayMilliseconds, token);
            EnsureForeground(frame.WindowHandle);
            NativeInput.ClickLeft(ClickHoldMilliseconds);
            _logger.Info($"已点击「接受」（屏幕坐标 {x},{y}）。");
        }
        finally
        {
            // 无论点击成功与否都要把背包关掉：留在背包界面里会挡住游戏后续操作。
            CloseBackpack(frame.WindowHandle);
        }
    }

    private void ProcessPickupScene(
        GrabbedFrame frame,
        SceneQuickActionsOptions options,
        DateTime now,
        CancellationToken token)
    {
        // 「拾取全部」文案在任何键位下都成立（模板已去掉按键框）；「快捷替换」按钮也算面板存在的证据。
        var hasAnchor = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.PickupAll,
            options.MatchThreshold,
            out var anchorHit);
        var hasQuickReplace = _matcher.TryMatch(
            frame.Gray,
            SceneQuickActionsTemplates.QuickReplace,
            options.MatchThreshold,
            out var quickReplaceHit);

        if (!hasAnchor && !hasQuickReplace)
        {
            if (!_pickupArmed)
                _pickupArmed = true;
            return;
        }

        if (!_pickupArmed || now < _pickupReadyAt)
            return;

        // 用鼠标点击「拾取全部」时必须先认到它的位置，认不到就再等下一帧，而不是乱点。
        if (!options.PickupAllBinding.IsConfigured && !hasAnchor)
        {
            LogThrottled("可拾取面板出现了，但没认到「拾取全部」的位置，本次不点击。");
            return;
        }

        _pickupArmed = false;
        _pickupReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
        PerformPickup(
            frame,
            hasAnchor ? anchorHit : null,
            hasQuickReplace ? quickReplaceHit : null,
            options,
            token);
    }

    private void PerformPickup(
        GrabbedFrame frame,
        MatchHit? pickupHit,
        MatchHit? quickReplaceHit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        var wokeMouse = false;
        if (options.PickupWakeMouse)
        {
            WakeMouse(options, frame.WindowHandle, token);
            wokeMouse = true;
        }

        try
        {
            // 第一步：拾取全部（按下配置的按键，未配置则点击提示位置）
            if (options.PickupAllBinding.IsConfigured)
            {
                EnsureForeground(frame.WindowHandle);
                NativeInput.InjectBinding(options.PickupAllBinding, KeyHoldMilliseconds);
                _logger.Info($"已按下「拾取全部」按键（{options.PickupAllBinding.DisplayName}）。");
            }
            else if (pickupHit is { } anchor)
            {
                var (x, y) = SceneQuickActionsMatcher.ToScreen(
                    anchor.CenterX,
                    anchor.CenterY,
                    frame.Bounds,
                    frame.Size);
                EnsureForeground(frame.WindowHandle);
                NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
                NativeInput.Sleep(PreClickDelayMilliseconds, token);
                EnsureForeground(frame.WindowHandle);
                NativeInput.ClickLeft(ClickHoldMilliseconds);
                _logger.Info($"已点击「拾取全部」（相似度 {anchor.Score:P0}，屏幕坐标 {x},{y}）。");
            }
            else
            {
                return;
            }

            NativeInput.Sleep(options.BetweenClicksMilliseconds, token);

            // 第二步：快捷替换
            if (options.QuickReplaceBinding.IsConfigured)
            {
                PressQuickReplaceKey(options, token);
                return;
            }

            ClickQuickReplace(frame, quickReplaceHit, options, token);
        }
        finally
        {
            // 切出过背包就必须关掉，否则会把拾取面板顶掉、还挡住后续操作。
            if (wokeMouse)
                CloseBackpack(frame.WindowHandle);
        }
    }

    /// <summary>「快捷替换」用按键：只需要确认面板还在，位置不重要。</summary>
    private void PressQuickReplaceKey(SceneQuickActionsOptions options, CancellationToken token)
    {
        for (var attempt = 0; attempt < QuickReplaceAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attempt > 0)
                NativeInput.Sleep(QuickReplaceRetryMilliseconds, token);

            if (!_grabber.TryGrab(out var fresh, out var failure, token) || fresh is null)
            {
                LogThrottled($"按下「快捷替换」按键前抓帧失败：{failure}");
                break;
            }

            using (fresh)
            {
                var panelOpen =
                    _matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.PickupAll,
                        options.MatchThreshold,
                        out _)
                    || _matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.QuickReplace,
                        options.MatchThreshold,
                        out _);
                if (!panelOpen)
                    continue;

                EnsureForeground(fresh.WindowHandle);
                NativeInput.InjectBinding(options.QuickReplaceBinding, KeyHoldMilliseconds);
                _logger.Info($"已按下「快捷替换」按键（{options.QuickReplaceBinding.DisplayName}）。");
                return;
            }
        }

        LogThrottled("没能确认可拾取面板仍在，已跳过「快捷替换」按键，避免误触其它操作。");
    }

    /// <summary>「快捷替换」用鼠标：必须重新认到按钮位置，绝不盲点。</summary>
    private void ClickQuickReplace(
        GrabbedFrame frame,
        MatchHit? quickReplaceHit,
        SceneQuickActionsOptions options,
        CancellationToken token)
    {
        for (var attempt = 0; attempt < QuickReplaceAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (attempt > 0)
                NativeInput.Sleep(QuickReplaceRetryMilliseconds, token);

            if (!_grabber.TryGrab(out var fresh, out var failure, token) || fresh is null)
            {
                LogThrottled($"「快捷替换」定位时抓帧失败：{failure}");
                break;
            }

            using (fresh)
            {
                if (!_matcher.TryMatch(
                        fresh.Gray,
                        SceneQuickActionsTemplates.QuickReplace,
                        options.MatchThreshold,
                        out var freshHit))
                {
                    continue;
                }

                ClickHit(fresh, freshHit, "快捷替换", token);
                return;
            }
        }

        // 拿不到新画面时，退回「拾取全部」那一帧里已经确认过的位置。
        if (quickReplaceHit is { } remembered)
        {
            ClickHit(frame, remembered, "快捷替换（上一帧位置）", token);
            return;
        }

        LogThrottled("面板里没有识别到「快捷替换」，已跳过第二步。");
    }

    private void ClickHit(GrabbedFrame frame, MatchHit hit, string label, CancellationToken token)
    {
        var (x, y) = SceneQuickActionsMatcher.ToScreen(hit.CenterX, hit.CenterY, frame.Bounds, frame.Size);
        EnsureForeground(frame.WindowHandle);
        NativeInput.MoveSmoothly(x, y, MoveDurationMilliseconds, token);
        NativeInput.Sleep(PreClickDelayMilliseconds, token);
        EnsureForeground(frame.WindowHandle);
        NativeInput.ClickLeft(ClickHoldMilliseconds);
        _logger.Info($"已点击「{label}」（相似度 {hit.Score:P0}，屏幕坐标 {x},{y}）。");
    }

    /// <summary>
    /// 切出鼠标：按一次 Tab 打开背包，把系统指针切出来。
    /// 之所以不用鼠标侧键：实测合成的侧键事件游戏不认（物理侧键正常），
    /// 而游戏自身键位（Tab）走键盘输入路径是通的。调用方负责成对调用
    /// <see cref="CloseBackpack"/> 把背包关掉。
    /// </summary>
    private void WakeMouse(SceneQuickActionsOptions options, IntPtr windowHandle, CancellationToken token)
    {
        EnsureForeground(windowHandle);
        NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
        _logger.Info("已按 Tab 打开背包切出鼠标。");

        // 下限 120ms：按完 Tab 背包还要一帧才真的开出来，太快移动会白动。
        var wait = Math.Max(options.WakeDelayMilliseconds, MinimumWakeDelayMilliseconds);
        NativeInput.Sleep(wait, token);
    }

    /// <summary>再按一次 Tab 把背包关掉，恢复游戏手势。游戏已不在前台时只记日志、不注入。</summary>
    private void CloseBackpack(IntPtr windowHandle)
    {
        if (!NativeInput.IsForegroundWindow(windowHandle))
        {
            _logger.Warning("游戏已不在前台，跳过「按 Tab 关闭背包」。");
            return;
        }

        try
        {
            NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
            _logger.Info("已再按一次 Tab 关闭背包。");
        }
        catch (Exception exception)
        {
            _logger.Warning($"关闭背包失败：{exception.Message}");
        }
    }

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

        try
        {
            if (!IsGameForeground())
            {
                LogThrottled("热键已按下，但当前前台窗口不是游戏，已忽略。");
                return;
            }

            var window = GetForegroundWindow();

            // 暂停「拾取相关」：默认 30 秒，免得把刚丢出来的东西又捡回去。
            var silence = Math.Clamp(
                options.DropSilenceSeconds,
                SceneQuickActionsOptions.DropSilenceMinimum,
                SceneQuickActionsOptions.DropSilenceMaximum);
            _dropSilenceUntil = DateTime.UtcNow.AddSeconds(silence);

            string[] work;
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
                work = BuildHotbarDropWork(ref cursor);
                Volatile.Write(ref _hotbarCursor, cursor);
                _logger.Info(
                    $"热键「逐个丢道具栏」触发：{work[0]}（先切出鼠标）；"
                    + $"「拾取相关」暂停 {silence} 秒。");
            }

            _ = Task.Run(() => RunDropWork(work, window, isBag));
        }
        finally
        {
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
        .. BagSlots.Select((_, index) => DescribeSlot("背包", index + 1))
    ];

    /// <summary>逐个丢道具栏：每按一次取下一个格子（1 → 2 → 3 → 4 循环）。</summary>
    private static string[] BuildHotbarDropWork(ref int cursor)
    {
        var slot = cursor % HotbarSlots.Length;
        cursor = (slot + 1) % HotbarSlots.Length;
        return [DescribeSlot("道具栏", slot + 1)];
    }

    private static string DescribeSlot(string kind, int index) => $"{kind}第 {index} 格";

    /// <summary>后台执行：切出鼠标 → 逐个拖到屏幕中心 → 恢复。</summary>
    private void RunDropWork(string[] labels, IntPtr window, bool isBag)
    {
        try
        {
            if (!TryGetClientBounds(window, out var bounds))
                return;

            // 两个功能都少了这一步：不切出鼠标，游戏里根本没有可拖的指针。
            // 功能一要先开背包（格子才在），功能二只要指针切出来。
            WakeMouseForDrop(window);

            var source = isBag
                ? BagSlots
                : [HotbarSlots[(Volatile.Read(ref _hotbarCursor) + HotbarSlots.Length - 1) % HotbarSlots.Length]];

            var originX = bounds.X;
            var originY = bounds.Y;
            var targetX = originX + (int)Math.Round(DropTargetX * bounds.Width);
            var targetY = originY + (int)Math.Round(DropTargetY * bounds.Height);

            // 拖拽快慢由设置页的「拖拽速度」档位决定。
            var timing = SceneQuickActionsOptions.GetDropTiming(
                Volatile.Read(ref _options).DropSpeedIndex);

            for (var index = 0; index < source.Length; index++)
            {
                if (!IsGameForeground())
                {
                    _logger.Warning("游戏已不在前台，放弃本次拖拽。");
                    return;
                }

                var fromX = originX + (int)Math.Round(source[index].X * bounds.Width);
                var fromY = originY + (int)Math.Round(source[index].Y * bounds.Height);
                DragOnce(fromX, fromY, targetX, targetY, timing);
                _logger.Info($"已把 {labels[index]} 拖到屏幕中心（{fromX},{fromY} → {targetX},{targetY}）。");

                if (index < source.Length - 1)
                    Thread.Sleep(timing.Interval);
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"拖拽失败：{exception.Message}");
        }
        finally
        {
            // 再把背包关回去，恢复游戏手势（再按一次 Tab 是开/关切换）。
            CloseBackpack(window);
        }
    }

    /// <summary>拖拽前切出鼠标：按一次 Tab 打开背包，等指针真正出现。</summary>
    private void WakeMouseForDrop(IntPtr window)
    {
        EnsureForeground(window);
        NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
        _logger.Info("已按 Tab 打开背包切出鼠标（准备拖拽）。");

        var options = Volatile.Read(ref _options);
        var wait = Math.Max(options.WakeDelayMilliseconds, MinimumWakeDelayMilliseconds);
        Thread.Sleep(wait);
    }

    /// <summary>按一次左键拖拽：移动 → 按下 → 缓动到目标 → 松手。快慢由 timing 决定。</summary>
    private static void DragOnce(
        int fromX,
        int fromY,
        int targetX,
        int targetY,
        (int Move, int Settle, int Interval) timing)
    {
        SetCursor(fromX, fromY);
        Thread.Sleep(timing.Settle);

        SendMouse(MouseeventfLeftdown);
        try
        {
            Thread.Sleep(timing.Settle);
            const int steps = 16;
            var perStep = Math.Max(1, timing.Move / steps);
            for (var step = 1; step <= steps; step++)
            {
                var progress = step / (double)steps;
                var eased = progress * progress * (3 - (2 * progress));
                SetCursor(
                    (int)Math.Round(fromX + ((targetX - fromX) * eased)),
                    (int)Math.Round(fromY + ((targetY - fromY) * eased)));
                Thread.Sleep(perStep);
            }
        }
        finally
        {
            SendMouse(MouseeventfLeftup);
        }
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

    private static void EnsureForeground(IntPtr windowHandle)
    {
        if (!NativeInput.IsForegroundWindow(windowHandle))
            throw new InvalidOperationException("游戏已不在前台，已放弃本次自动点击。");
    }

    private static bool IsIdleFailure(string failure) =>
        string.IsNullOrWhiteSpace(failure)
        || failure.Contains("前台", StringComparison.Ordinal)
        || failure.Contains("dwrg", StringComparison.OrdinalIgnoreCase);

    private void LogThrottled(string message)
    {
        var now = DateTime.UtcNow;
        if (string.Equals(message, _lastWarning, StringComparison.Ordinal)
            && (now - _lastWarningAt).TotalSeconds < WarningThrottleSeconds)
        {
            return;
        }

        _lastWarning = message;
        _lastWarningAt = now;
        _logger.Warning(message);
    }
}
