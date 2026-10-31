using System.Diagnostics;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.PickupEscape;

/// <summary>
/// 「关闭拾取面板」的工作线程：按检测间隔抓一帧，然后
///   ① 先用「退出战斗」模板认游戏设置界面（误开出来的那层）：认出就补按一下 ESC 退回去；
///   ② 再用「背包辅助」那两张模板确认「可拾取」面板在不在 —— 在、并且**连续存在够久**
///      （默认 250ms，一闪而过的不算）、且这一轮还没按过，才按一下 ESC。
///
/// 「面板连续出现够久」是第一道防线（人已经走开时那一下 ESC 根本不发出去），
/// 「认出设置界面补按 ESC」是第二道防线（万一还是开出来了，替你退回去）。
/// 「展开拾取列表」（默认 H）之后的让路期内连帧都不抓（省资源、也不会误按）。
///
/// 只有 dwrg.exe 是前台窗口时才抓帧；宿主地图界面打开时暂停检测；
/// 每次注入前重新校验前台，游戏切走了就放弃这一次。
///
/// 另外负责那个开关热键：按一下暂停检测、再按一下恢复（默认未设置键位）。
/// 线程只在插件启用期间存在，暂停检测时只空转等待、不抓帧。
/// </summary>
internal sealed class PickupEscapeRunner : IDisposable
{
    private const int KeyHoldMilliseconds = 30;
    private const int WarningThrottleSeconds = 20;
    private const int HotkeyDebounceMilliseconds = 400;

    private readonly object _sync = new();
    private readonly PickupEscapeFrameGrabber _grabber;
    private readonly PickupEscapeMatcher _matcher;
    private readonly IPluginLogger _logger;

    private PickupEscapeOptions _options = new();
    private CancellationTokenSource? _cancellation;
    private Thread? _worker;
    private volatile bool _mapOpen;
    private int _detectionEnabled = 1;
    /// <summary>「面板刚出现」才动作：面板消失后重新武装，避免面板还在时被反复按 ESC。</summary>
    private bool _armed = true;
    private DateTime _readyAt = DateTime.MinValue;
    /// <summary>本次「可拾取」面板第一次被认出来的时刻（UTC ticks；0 = 当前没看到面板）。</summary>
    private long _panelFirstSeenTicks;
    /// <summary>「误开设置界面 → 补一下 ESC」的武装状态与冷却。</summary>
    private bool _settingsEscapeArmed = true;
    private DateTime _settingsEscapeReadyAt = DateTime.MinValue;
    /// <summary>「展开拾取列表」后的暂停截止时刻（UTC ticks；0 = 没在暂停）。</summary>
    private long _escapePausedUntilTicks;
    private int _pauseGate;
    private string _lastWarning = string.Empty;
    private DateTime _lastWarningAt = DateTime.MinValue;
    private bool _disposed;

    // 热键状态（走宿主的 IPluginInputService，键盘键与鼠标键都能绑）
    private IPluginInputService? _hotkeyService;
    private string _pluginId = PickupEscapeOptions.PluginId;
    private long _hotkeyBindingsUpdatedAt;
    private DateTime _lastHotkeyAt = DateTime.MinValue;
    private int _hotkeyBusy;

    public PickupEscapeRunner(
        PickupEscapeFrameGrabber grabber,
        PickupEscapeMatcher matcher,
        IPluginLogger logger)
    {
        _grabber = grabber ?? throw new ArgumentNullException(nameof(grabber));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>检测开关被热键切换后触发，交给插件去发游戏内浮层提示。</summary>
    public event Action<bool>? DetectionStateChanged;

    public bool DetectionEnabled => Volatile.Read(ref _detectionEnabled) == 1;

    public void SetOptions(PickupEscapeOptions options) =>
        Volatile.Write(ref _options, options ?? new PickupEscapeOptions());

    /// <summary>宿主地图界面打开时暂停检测：那时不可能出现拾取面板，也避免与识别流程抢抓帧。</summary>
    public void SetMapOpen(bool mapOpen) => _mapOpen = mapOpen;

    /// <summary>插件刚启用 / 重新启用时回到「检测运行中」。</summary>
    public void ResetDetection()
    {
        Volatile.Write(ref _detectionEnabled, 1);
        _armed = true;
        _readyAt = DateTime.MinValue;
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is { IsAlive: true })
                return;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "IDVB pickup-escape"
            };
            _worker.Start();
            _lastHotkeyAt = DateTime.MinValue;
            StartHotkeys();
        }

        _logger.Info($"关闭拾取面板已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
    }

    public void Stop()
    {
        Thread? worker;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            worker = _worker;
            cancellation = _cancellation;
            _worker = null;
            _cancellation = null;
            StopHotkeys();
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

        _armed = true;
        _readyAt = DateTime.MinValue;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        Stop();
        lock (_sync)
            _hotkeyService = null;
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var options = Volatile.Read(ref _options);
            try
            {
                // 「展开拾取列表」后的让路期内连帧都不抓：省资源，也不可能误按 ESC。
                if (DetectionEnabled && !_mapOpen && !IsEscapePaused)
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

    private void ProcessOnce(PickupEscapeOptions options, CancellationToken token)
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

            // ① 误开的游戏设置界面优先处理：那一下 ESC 没东西可关时游戏会开到这一层 ——
            //    认出它再补一下 ESC 退回去（面板稳定门槛是第一道防线，这里是第二道）。
            if (options.EscapeSettingsEnabled)
            {
                var hasSettings = _matcher.TryMatch(
                    frame.Gray,
                    PickupEscapeTemplates.SettingsScreen,
                    options.MatchThreshold,
                    out var settingsHit);
                if (hasSettings)
                {
                    if (_settingsEscapeArmed && now >= _settingsEscapeReadyAt)
                    {
                        _settingsEscapeArmed = false;
                        _settingsEscapeReadyAt = now.AddMilliseconds(options.CooldownMilliseconds);
                        PerformEscape(frame, settingsHit, "游戏设置界面", options, token);
                    }
                    // 设置界面还在的时候，不去碰「可拾取」那条线。
                    return;
                }

                // 设置界面不在了：重新武装，等它下次出现再补一下。
                _settingsEscapeArmed = true;
            }

            // ② 「拾取全部」文案在任何键位下都成立（模板已去掉按键框）；「快捷替换」按钮也算面板存在的证据。
            var hasAnchor = _matcher.TryMatch(
                frame.Gray,
                PickupEscapeTemplates.PickupAll,
                options.MatchThreshold,
                out var anchorHit);
            var hasQuickReplace = _matcher.TryMatch(
                frame.Gray,
                PickupEscapeTemplates.QuickReplace,
                options.MatchThreshold,
                out var quickReplaceHit);

            if (!hasAnchor && !hasQuickReplace)
            {
                // 面板不在了：重新武装、重新计时，等它下次出现再说。
                _armed = true;
                _panelFirstSeenTicks = 0;
                return;
            }

            // ③ 面板得「连续存在够久」才算数：一闪而过（人已经走开）就不按 ESC ——
            //    那种情况下 ESC 没东西可关，按下去反而会把游戏设置界面开出来。
            if (_panelFirstSeenTicks == 0)
                _panelFirstSeenTicks = now.Ticks;
            if (!HasBeenVisibleLongEnough(
                    _panelFirstSeenTicks,
                    now,
                    options.MinimumVisibleMilliseconds))
            {
                var visibleMilliseconds =
                    (now.Ticks - _panelFirstSeenTicks) / (double)TimeSpan.TicksPerMillisecond;
                LogThrottled(
                    $"「可拾取」面板才出现 {visibleMilliseconds:F0}ms，"
                    + $"还没到 {options.MinimumVisibleMilliseconds}ms，本次不按 ESC。");
                return;
            }

            // ④ 面板稳定存在够久了，而且是这一轮刚出现的：按 ESC 关掉它。
            if (!_armed || now < _readyAt)
                return;

            _armed = false;
            _readyAt = now.AddMilliseconds(options.CooldownMilliseconds);
            PerformEscape(
                frame,
                hasAnchor ? anchorHit : quickReplaceHit,
                "「可拾取」面板",
                options,
                token);
        }
    }

    /// <summary>
    /// 面板是否已经连续存在够久（毫秒）。<paramref name="minimumVisibleMilliseconds"/> = 0 表示不设门槛；
    /// <paramref name="firstSeenTicks"/> = 0 表示「当前没看到面板」。
    /// </summary>
    internal static bool HasBeenVisibleLongEnough(
        long firstSeenTicks,
        DateTime now,
        int minimumVisibleMilliseconds)
    {
        if (firstSeenTicks == 0)
            return false;

        var visibleMilliseconds =
            (now.Ticks - firstSeenTicks) / (double)TimeSpan.TicksPerMillisecond;
        return visibleMilliseconds >= Math.Max(0, minimumVisibleMilliseconds);
    }

    /// <summary>目标确认在（「可拾取」面板或设置界面）→ 按一下 ESC（或用户在设置页录的键）。</summary>
    private void PerformEscape(
        GrabbedFrame frame,
        MatchHit hit,
        string label,
        PickupEscapeOptions options,
        CancellationToken token)
    {
        if (!options.ActionBinding.IsConfigured)
        {
            LogThrottled($"识别到{label}，但设置页里没有可用的按键，本次不动作。");
            return;
        }

        EnsureForeground(frame.WindowHandle);
        PickupEscapeNativeInput.InjectBinding(options.ActionBinding, KeyHoldMilliseconds);
        token.ThrowIfCancellationRequested();
        PickupEscapeNativeInput.Sleep(options.GetDelayAfterStep(0), token);
        _logger.Info(
            $"识别到{label}（相似度 {hit.Score:P0}），已按下 {options.ActionBinding.DisplayName}。");
    }

    private static void EnsureForeground(IntPtr windowHandle)
    {
        if (!PickupEscapeNativeInput.IsForegroundWindow(windowHandle))
            throw new InvalidOperationException("游戏已不在前台，已放弃本次按键。");
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

    // ════════════════ 开关热键 ════════════════

    /// <summary>热键状态切换：返回切换后的状态（true = 检测运行中）。</summary>
    public bool ToggleDetection()
    {
        if (Interlocked.CompareExchange(ref _hotkeyBusy, 1, 0) != 0)
            return DetectionEnabled;

        try
        {
            var enabled = Volatile.Read(ref _detectionEnabled) == 0;
            Volatile.Write(ref _detectionEnabled, enabled ? 1 : 0);
            // 恢复检测时让下一次命中可以立即动作，不必等上一轮的冷却。
            _readyAt = DateTime.MinValue;
            _logger.Info(enabled
                ? "热键：已恢复检测。"
                : "热键：已暂停检测（再按一次恢复）。");
            return enabled;
        }
        finally
        {
            Interlocked.Exchange(ref _hotkeyBusy, 0);
        }
    }

    /// <summary>接入宿主的插件输入通道：热键的按键监听、注入过滤都由宿主负责。</summary>
    public void AttachHotkeys(IPluginInputService? input, string pluginId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_hotkeyService, input)
                && string.Equals(_pluginId, pluginId, StringComparison.Ordinal))
            {
                return;
            }

            StopHotkeys();
            _hotkeyService = input;
            _pluginId = pluginId;
            if (_cancellation is not null)
                StartHotkeys();
        }
    }

    /// <summary>热键改动后重新注册（设置页改键位时调用）。</summary>
    public void RefreshHotkeyBindings() => RegisterHotkeys();

    private void OnHotkeyInvoked(object? sender, PluginInputEventArgs args)
    {
        if (_disposed
            || _cancellation is null
            || !ReferenceEquals(sender, _hotkeyService)
            || args.Timestamp < _hotkeyBindingsUpdatedAt
            || !string.Equals(args.PluginId, _pluginId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var options = Volatile.Read(ref _options);

        // 「展开拾取列表」的按键：按下后暂停「按 ESC」若干秒（抬起不处理）。
        if (string.Equals(args.BindingKey, PickupEscapeOptions.PickupListBindingKey, StringComparison.Ordinal)
            && options.PickupListBinding.IsConfigured)
        {
            if (args.IsDown)
                PauseEscapeForPickupList();
            return;
        }

        if (!args.IsDown
            || !options.ToggleHotkey.IsConfigured
            || !string.Equals(args.BindingKey, PickupEscapeOptions.ToggleHotkeyKey, StringComparison.Ordinal))
        {
            return;
        }

        // 防抖：按住不放时只认第一次
        var now = DateTime.UtcNow;
        if (now < _lastHotkeyAt.AddMilliseconds(HotkeyDebounceMilliseconds))
            return;
        _lastHotkeyAt = now;

        var enabled = ToggleDetection();
        DetectionStateChanged?.Invoke(enabled);
    }

    private void StartHotkeys()
    {
        if (_hotkeyService is null)
            return;

        _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
        _hotkeyService.BindingInvoked += OnHotkeyInvoked;
        RegisterHotkeys();
    }

    private void StopHotkeys()
    {
        if (_hotkeyService is null)
            return;

        _hotkeyService.BindingInvoked -= OnHotkeyInvoked;
        _hotkeyService.ClearBindings(_pluginId);
    }

    private void RegisterHotkeys()
    {
        lock (_sync)
        {
            if (_disposed || _cancellation is null || _hotkeyService is null)
                return;

            var options = Volatile.Read(ref _options);
            _hotkeyService.SetBinding(_pluginId, PickupEscapeOptions.ToggleHotkeyKey, options.ToggleHotkey);
            _hotkeyService.SetBinding(_pluginId, PickupEscapeOptions.PickupListBindingKey, options.PickupListBinding);
            // 宿主事件使用 Stopwatch 时间戳，可能仍在 UI 队列中等待分发。
            _hotkeyBindingsUpdatedAt = Stopwatch.GetTimestamp();
            _logger.Info(options.ToggleHotkey.IsConfigured
                ? $"开关检测热键已注册：{options.ToggleHotkey.DisplayName}。"
                : "开关检测热键尚未设置（去插件设置里录一个键，就能随时暂停/恢复检测）。");
            if (options.PickupListBinding.IsConfigured)
            {
                _logger.Info(
                    $"「展开拾取列表」按键已注册：{options.PickupListBinding.DisplayName}"
                    + $"（按下后 {options.PauseAfterPickupListSeconds} 秒内不按 ESC）。");
            }
        }
    }

    // ════════════════ 「展开拾取列表」后的暂停 ════════════════

    /// <summary>「展开拾取列表」后的暂停截止时刻（UTC）。</summary>
    internal DateTime EscapePausedUntil =>
        new(Volatile.Read(ref _escapePausedUntilTicks), DateTimeKind.Utc);

    /// <summary>当前是否处在「展开拾取列表」后的暂停期内。</summary>
    internal bool IsEscapePaused =>
        DateTime.UtcNow.Ticks < Volatile.Read(ref _escapePausedUntilTicks);

    /// <summary>
    /// 用户按下了「展开拾取列表」（默认 H）：接下来若干秒内不按 ESC，
    /// 免得把你刚展开的列表立刻关掉。暂停结束后不补动作，要等面板消失再出现才会动作。
    /// </summary>
    internal void PauseEscapeForPickupList()
    {
        if (Interlocked.CompareExchange(ref _pauseGate, 1, 0) != 0)
            return;

        try
        {
            var options = Volatile.Read(ref _options);
            var seconds = Math.Clamp(
                options.PauseAfterPickupListSeconds,
                PickupEscapeOptions.PauseSecondsMinimum,
                PickupEscapeOptions.PauseSecondsMaximum);
            Volatile.Write(
                ref _escapePausedUntilTicks,
                DateTime.UtcNow.AddSeconds(seconds).Ticks);
            // 暂停期内已经认出的面板不补动作：恢复后要等它消失再出现。
            _armed = false;
            _panelFirstSeenTicks = 0;
            _logger.Info(
                $"检测到「展开拾取列表」（{options.PickupListBinding.DisplayName}）："
                + $"{seconds} 秒内不按 ESC。");
        }
        finally
        {
            Interlocked.Exchange(ref _pauseGate, 0);
        }
    }
}
