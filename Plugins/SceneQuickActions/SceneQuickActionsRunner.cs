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
internal sealed partial class SceneQuickActionsRunner : IDisposable
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

    private const int BagSlotCount = 6;
    private const int HotbarSlotCount = 4;

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
                Name = "IDVB scene-quick-actions"
            };
            _worker.Start();
            _lastHotkeyAt = DateTime.MinValue;
            StartHotkeys();
        }

        _logger.Info($"背包辅助已启动（抓帧路径：{(_grabber.HasFastPath ? "宿主快速抓帧" : "PNG 截图")}）。");
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

        _inviteArmed = true;
        _pickupArmed = true;
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

    /// <summary>
    /// 切出鼠标：按一次 Tab 打开背包，把系统指针切出来。
    /// 之所以不用鼠标侧键：实测合成的侧键事件游戏不认（物理侧键正常），
    /// 而游戏自身键位（Tab）走键盘输入路径是通的。调用方负责成对调用
    /// <see cref="CloseBackpack"/> 把背包关掉。
    /// </summary>
    private void WakeMouse(SceneQuickActionsOptions options, IntPtr windowHandle, CancellationToken token,
        ref bool wokeMouse)
    {
        EnsureForeground(windowHandle);
        NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
        wokeMouse = true;
        _logger.Info("已按 Tab 打开背包切出鼠标。");

        // 下限 120ms：按完 Tab 背包还要一帧才真的开出来，太快移动会白动。
        var wait = Math.Max(options.WakeDelayMilliseconds, MinimumWakeDelayMilliseconds);
        SleepAfterStep(options, wait, token);
    }

    /// <summary>再按一次 Tab 把背包关掉，恢复游戏手势。游戏已不在前台时只记日志、不注入。</summary>
    private void CloseBackpack(IntPtr windowHandle, SceneQuickActionsOptions options)
    {
        if (!NativeInput.IsForegroundWindow(windowHandle))
        {
            _logger.Warning("游戏已不在前台，跳过「按 Tab 关闭背包」。");
            return;
        }

        try
        {
            NativeInput.InjectKey(SceneQuickActionsOptions.WakeVirtualKey, WakeHoldMilliseconds);
            SleepAfterStep(options, 0);
            _logger.Info("已再按一次 Tab 关闭背包。");
        }
        catch (Exception exception)
        {
            _logger.Warning($"关闭背包失败：{exception.Message}");
        }
    }

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
