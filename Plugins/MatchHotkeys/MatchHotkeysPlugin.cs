using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Plugins.MatchHotkeys;

/// <summary>
/// 对局快捷键：五个可自定义的快捷键，各自绑定一个地图模式（地图 Class）。
///
/// 按一下「进入该模式的对局」，再按一下「结束对局」；判定依据是宿主当前对局
/// 是否正好是这个模式，因此不会误把别的模式的对局当成自己的开关。
/// 槽位数量由 <see cref="MatchHotkeysOptions.SlotCount"/> 决定，主体与设置页都按表
/// 遍历生成；用不到的槽位保持未设置，按下不会有任何动作。
///
/// 全程走宿主既有的插件对局控制通道（<see cref="IHostMatchCapability"/> →
/// SessionOrchestrator 的 QueuePluginMatchAsync），所以对局门控、地图缓存同步、
/// 生命周期锁都沿用宿主原有的规则，本插件不碰识别管线与游戏进程。
///
/// 常驻（<c>AlwaysActive</c>）：开局前宿主会关掉非常驻插件的运行时闸门，
/// 若非常驻，这个「用来开局的快捷键」在局外就永远不会被激活。
/// </summary>
[Plugin(
    MatchHotkeysOptions.PluginId,
    DisplayName = "对局快捷键",
    Description = "五个可自定义的快捷键，各自一键进入/结束指定地图模式的对局；地图模式在插件设置里选。",
    Version = "1.3.1",
    AlwaysActive = true)]
public sealed partial class MatchHotkeysPlugin
    : PluginBase, IPluginSettingsProvider, IHandle<MatchStateChangedMessage>
{
    /// <summary>地图库两次刷新之间的最短间隔，避免反复读目录。</summary>
    private const long MapClassRefreshCooldownMilliseconds = 3000;

    /// <summary>首次渲染设置描述符时，同步等地图库的上限（见 RefreshMapClassesForSettings）。</summary>
    private const int MapClassSyncWaitMilliseconds = 3000;

    /// <summary>
    /// 之后每次渲染设置描述符时，同步等地图库的上限。地图库有修订版本缓存，
    /// 命中时几乎立即返回；这里只是给「恰好赶上目录正在重写」留个退路。
    /// </summary>
    private const int MapClassSettingsSyncWaitMilliseconds = 800;

    /// <summary>
    /// 同一批渲染内复用地图库结果的时间窗。宿主渲染一次设置页会连续读若干次
    /// <see cref="Settings"/>，有了这个窗口就不会每次都去读一遍目录；
    /// 隔开更久再打开设置页，则一定会重新读。
    /// </summary>
    private const long MapClassSettingsRefreshWindowMilliseconds = 500;

    private const int OperationPollMilliseconds = 80;

    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private IPluginInputService? _input;
    private IPluginGameWindowService? _gameWindow;
    private IHostMatchCapability? _matches;
    private ISessionOrchestrator? _session;
    private IGameOverlayToast? _toast;
    private MatchHotkeysOptions _options = MatchHotkeysOptions.Default;
    private string[] _mapClasses = [];
    private bool _mapClassSyncAttempted;
    private int _mapClassRefreshRunning;
    private long _mapClassesRefreshedTick;
    private bool _loaded;
    private bool _enabled;
    private int _switching;
    /// <summary>最近一次对局快照里的地图模式；只在 IsMatchStarted 为真时用于判定「是否就是本快捷键的目标」。</summary>
    private string? _activeMatchClass;

    public override string Id => MatchHotkeysOptions.PluginId;

    public override string DisplayName => "对局快捷键";

    // ════════════════ 生命周期 ════════════════

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _loaded = true;

        _input = context.GetService<IPluginInputService>();
        _gameWindow = context.GetService<IPluginGameWindowService>();
        _matches = context.GetService<IHostMatchCapability>();
        _session = context.GetService<ISessionOrchestrator>();
        _toast = context.GetService<IGameOverlayToast>();

        if (_input is null)
            context.Logger.Error("宿主没有提供插件输入通道（IPluginInputService），对局快捷键不可用。");
        if (_matches is null)
            context.Logger.Error("宿主没有提供对局控制能力（IHostMatchCapability），对局快捷键不可用。");
        if (_toast is null)
            context.Logger.Warning("宿主没有提供浮层提示通道，进/出对局时屏幕上不会有提示（功能不受影响）。");
        if (_gameWindow is null)
            context.Logger.Warning("宿主没有提供前台游戏窗口服务，「仅在游戏窗口前台时生效」这道护栏本次不生效。");

        RequestMapClassRefresh();
    }

    public override void OnEnable()
    {
        _enabled = true;
        if (_input is not null)
            _input.BindingInvoked += OnBindingInvoked;
        ApplyHotkeyBindings();
        RequestMapClassRefresh();

        var options = _options;
        LogInfo(DescribeStartup(options));
    }

    /// <summary>启动日志：只列出真的录了键（或选了地图）的槽位，全空时只记一条提示。</summary>
    private static string DescribeStartup(MatchHotkeysOptions options)
    {
        var parts = new List<string>(MatchHotkeysOptions.SlotCount);
        for (var slot = 0; slot < MatchHotkeysOptions.SlotCount; slot++)
        {
            var binding = options.HotkeyAt(slot);
            var target = options.MapClassAt(slot);
            if (!binding.IsConfigured && MatchHotkeysOptions.IsUnsetClass(target))
                continue;
            parts.Add(
                $"{MatchHotkeysOptions.SlotNameAt(slot)} "
                + $"{(binding.IsConfigured ? binding.DisplayName : "未设置")} → {DescribeClass(target)}");
        }

        var described = parts.Count == 0
            ? $"{MatchHotkeysOptions.SlotCount} 个快捷键都还没设置"
            : string.Join("；", parts);
        return $"对局快捷键已启动：{described}；仅游戏内生效 {(options.ForegroundOnly ? "开" : "关")}。";
    }

    public override void OnDisable()
    {
        _enabled = false;
        if (_input is not null)
        {
            _input.BindingInvoked -= OnBindingInvoked;
            _input.ClearBindings(Id);
        }
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_input is not null)
            _input.BindingInvoked -= OnBindingInvoked;
    }

    /// <summary>宿主每次对局状态变化都会带上当前地图模式，用来判定「再按一下是结束还是换图」。</summary>
    public void Handle(MatchStateChangedMessage message) => _activeMatchClass = message.MapClass;

    // ════════════════ 热键 ════════════════

    private void ApplyHotkeyBindings()
    {
        if (_input is not { } input)
            return;
        var options = _options;
        // 未配置的槽位也要写一次（none），宿主据此把该绑定键清空；不影响同一插件的其它绑定。
        for (var slot = 0; slot < MatchHotkeysOptions.SlotCount; slot++)
            ApplyHotkeyBinding(input, MatchHotkeysOptions.HotkeyKeyAt(slot), options.HotkeyAt(slot));
    }

    private void ApplyHotkeyBinding(IPluginInputService input, string bindingKey, PluginInputBinding binding)
    {
        try
        {
            // 未配置时也要写一次，宿主据此把该绑定键清空（不影响本插件的其它绑定）。
            input.SetBinding(Id, bindingKey, binding);
        }
        catch (Exception exception)
        {
            LogWarning($"注册热键失败（{bindingKey}）：{Describe(exception)}");
        }
    }

    private void OnBindingInvoked(object? sender, PluginInputEventArgs args)
    {
        if (!string.Equals(args.PluginId, Id, StringComparison.Ordinal) || !args.IsDown)
            return;
        var slot = MatchHotkeysOptions.SlotOfHotkeyKey(args.BindingKey);
        if (slot < 0)
            return;
        _ = ToggleMatchAsync(slot);
    }

    private async Task ToggleMatchAsync(int slot)
    {
        var options = _options;
        var slotName = MatchHotkeysOptions.SlotNameAt(slot);
        var binding = options.HotkeyAt(slot);
        var target = options.MapClassAt(slot).Trim();

        if (MatchHotkeysOptions.IsUnsetClass(target))
        {
            LogWarning($"{slotName}还没有在设置里选择地图模式，已忽略这次按键。");
            Toast(warning: true, $"{slotName}还没选择地图模式");
            return;
        }
        if (_matches is not { } matches)
        {
            LogWarning($"{slotName}：宿主没有提供对局控制能力，已忽略这次按键。");
            return;
        }
        // 目标被删掉 / 被地图包更新替换后就成了「幽灵目标」：下拉里已经没有它，
        // 直接交给宿主只会拿到「没有此模式：X」。这里先拦一次，给一条能照做的提示。
        if (!IsTargetInCatalog(target))
        {
            LogWarning($"{slotName}的目标「{target}」已不在地图库里，已忽略这次按键。");
            Toast(warning: true, $"{slotName}的目标已失效，请到插件设置里重新选择地图模式");
            return;
        }
        if (options.ForegroundOnly && !IsGameForeground())
        {
            LogInfo($"{slotName}：当前前台不是游戏窗口，已忽略这次按键（「仅在游戏窗口前台时生效」已打开）。");
            return;
        }
        if (Interlocked.Exchange(ref _switching, 1) != 0)
        {
            LogInfo($"{slotName}：上一次对局切换还没结束，已忽略这次按键。");
            return;
        }

        try
        {
            var alreadyInTargetMatch = _session?.IsMatchStarted == true
                && !string.IsNullOrEmpty(_activeMatchClass)
                && string.Equals(_activeMatchClass, target, StringComparison.Ordinal);
            var startedAt = DateTime.UtcNow;
            var result = alreadyInTargetMatch
                ? await matches.EndAsync(CancellationToken.None).ConfigureAwait(false)
                : await matches.SwitchAndBeginAsync(
                    new HostMatchRequest { MapClass = target, EndCurrentMatch = true },
                    CancellationToken.None).ConfigureAwait(false);
            result = await AwaitCompletionAsync(matches, result).ConfigureAwait(false);
            var elapsed = (int)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            if (result.State == HostOperationState.Applied)
            {
                LogInfo($"{slotName}（{binding.DisplayName}）：{result.Message}（{elapsed}ms）");
                // 结束对局时宿主 EndMatchCoreAsync 自己会弹「已结束对局状态。」，
                // 插件再弹一条就是屏幕上两个黄框。只有「进入对局」宿主没有提示，由插件补一条。
                if (!alreadyInTargetMatch)
                    Toast(warning: false, $"已进入对局 · {target}");
            }
            else
            {
                LogWarning($"{slotName}（{binding.DisplayName}）：{result.State} · {result.Message}");
                // 被拒绝 / 宿主没就绪 / 失败都值得让用户看见；Busy（连按同一下）不刷屏。
                if (result.State is HostOperationState.Rejected
                    or HostOperationState.Failed
                    or HostOperationState.NotReady)
                {
                    Toast(warning: true, $"{slotName}：{result.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            LogWarning($"{slotName}：对局切换失败 · {Describe(exception)}");
            Toast(warning: true, $"{slotName}：对局切换失败");
        }
        finally
        {
            Volatile.Write(ref _switching, 0);
        }
    }

    /// <summary>宿主的开局/结束是「先受理、后执行」，必须轮询到最终结果才知道成没成。</summary>
    private static async Task<HostOperationResult> AwaitCompletionAsync(
        IHostMatchCapability matches,
        HostOperationResult result)
    {
        var deadline = DateTime.UtcNow + OperationTimeout;
        while (result.State == HostOperationState.Accepted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(OperationPollMilliseconds).ConfigureAwait(false);
            result = await matches.GetOperationAsync(result.OperationId, CancellationToken.None)
                .ConfigureAwait(false)
                ?? new HostOperationResult(
                    result.OperationId, HostOperationState.Failed, "对局操作记录已不可用。");
        }

        return result.State == HostOperationState.Accepted
            ? new HostOperationResult(result.OperationId, HostOperationState.Failed, "对局操作超时。")
            : result;
    }

    /// <summary>
    /// 前台是不是游戏：直接用宿主已验证的「前台游戏客户区」服务
    /// （它只在 dwrg.exe 是前台窗口时才返回成功），因此与本体其它插件的判定完全一致。
    /// </summary>
    private bool IsGameForeground()
    {
        if (_gameWindow is not { } gameWindow)
            return true;
        try
        {
            return gameWindow.TryGetForegroundClientBounds(out _, out _, out _);
        }
        catch (Exception exception)
        {
            LogWarning($"前台窗口检测失败，本次不做拦截：{Describe(exception)}");
            return true;
        }
    }

    // ════════════════ 浮层提示 ════════════════

    /// <summary>发一条游戏内浮层提示；提示本身出问题也绝不能影响对局切换。</summary>
    private void Toast(bool warning, string message)
    {
        try
        {
            if (_toast is not { } toast)
                return;
            if (warning)
                toast.Warning(message);
            else
                toast.Notice(message);
        }
        catch (Exception exception)
        {
            LogWarning($"浮层提示发送失败：{Describe(exception)}");
        }
    }

    // ════════════════ 日志小工具 ════════════════

    private void LogInfo(string message)
    {
        if (_loaded)
            Context.Logger.Info(message);
    }

    private void LogWarning(string message)
    {
        if (_loaded)
            Context.Logger.Warning(message);
    }

    private static string DescribeClass(string? mapClass) =>
        MatchHotkeysOptions.IsUnsetClass(mapClass) ? "（未指定）" : mapClass!.Trim();

    private static string Describe(Exception exception) =>
        exception.GetBaseException().Message;
}
