using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Plugins.MatchHotkeys;

/// <summary>
/// 对局快捷键：两个可自定义的快捷键，各自绑定一个地图模式（地图 Class）。
///
/// 按一下「进入该模式的对局」，再按一下「结束对局」；判定依据是宿主当前对局
/// 是否正好是这个模式，因此不会误把别的模式的对局当成自己的开关。
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
    Description = "两个可自定义的快捷键，各自一键进入/结束指定地图模式的对局；地图模式在插件设置里选。",
    Version = "1.2.0",
    AlwaysActive = true)]
public sealed class MatchHotkeysPlugin
    : PluginBase, IPluginSettingsProvider, IHandle<MatchStateChangedMessage>
{
    /// <summary>地图库两次刷新之间的最短间隔，避免反复读目录。</summary>
    private const long MapClassRefreshCooldownMilliseconds = 3000;

    /// <summary>首次渲染设置描述符时，同步等地图库的上限（见 EnsureMapClassesForFirstRender）。</summary>
    private const int MapClassSyncWaitMilliseconds = 3000;

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

    // ════════════════ 设置描述符 ════════════════

    /// <summary>
    /// 每次访问都重建描述符：地图模式下拉框的候选项来自宿主地图库，
    /// 只有在打开设置页时读一次才能反映用户新装/新订阅的地图。
    /// </summary>
    public IReadOnlyList<IPluginSetting> Settings => BuildSettings();

    private IReadOnlyList<IPluginSetting> BuildSettings()
    {
        EnsureMapClassesForFirstRender();
        var options = _options;
        var classOptionsA = ClassOptionsFor(options.MapClassA);
        var classOptionsB = ClassOptionsFor(options.MapClassB);
        return
        [
            // 两个快捷键收进「按键」折叠区（默认收起），名字里不带任何括号说明。
            new PluginKeyBindingSetting
            {
                Key = MatchHotkeysOptions.HotkeyAKey,
                DisplayName = "快捷键一",
                Group = MatchHotkeysOptions.KeyGroupTitle,
                DefaultValue = MatchHotkeysOptions.NoBindingStorageValue,
                AllowedKinds = PluginInputBindingKinds.All
            },
            new PluginKeyBindingSetting
            {
                Key = MatchHotkeysOptions.HotkeyBKey,
                DisplayName = "快捷键二",
                Group = MatchHotkeysOptions.KeyGroupTitle,
                DefaultValue = MatchHotkeysOptions.NoBindingStorageValue,
                AllowedKinds = PluginInputBindingKinds.All
            },
            // 两个目标收进「目标」折叠区（默认收起）。
            new PluginChoiceSetting
            {
                Key = MatchHotkeysOptions.MapClassAKey,
                DisplayName = "地图 A",
                Description = "按一下进入，再按一下结束。",
                Group = MatchHotkeysOptions.TargetGroupTitle,
                Options = classOptionsA,
                DefaultIndex = IndexOfOption(classOptionsA, options.MapClassA)
            },
            new PluginChoiceSetting
            {
                Key = MatchHotkeysOptions.MapClassBKey,
                DisplayName = "地图 B",
                Description = "按一下进入，再按一下结束。",
                Group = MatchHotkeysOptions.TargetGroupTitle,
                Options = classOptionsB,
                DefaultIndex = IndexOfOption(classOptionsB, options.MapClassB)
            },
            // 未分组的项会被宿主平铺在设置页最上面（折叠区之上）。
            new PluginToggleSetting
            {
                Key = MatchHotkeysOptions.ForegroundOnlyKey,
                DisplayName = "仅在游戏窗口前台时生效",
                Description = "非游戏前台时按键无效。",
                DefaultValue = true
            }
        ];
    }

    /// <summary>
    /// 下拉候选＝「（请选择地图模式）」＋地图库里的全部模式；当前已保存的模式一定保留在列表里，
    /// 免得它被回退掉。
    /// </summary>
    private string[] ClassOptionsFor(string current)
    {
        string[] classes;
        lock (_gate)
            classes = _mapClasses;

        var list = new List<string>(classes.Length + 2)
        {
            MatchHotkeysOptions.UnsetClassOption
        };
        list.AddRange(classes);
        var trimmed = current?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && !list.Contains(trimmed, StringComparer.Ordinal))
        {
            list.Add(trimmed);
        }
        return [.. list];
    }

    /// <summary>地图库确实读到了内容（用它区分「地图库暂时读不到」与「用户清空选择」）。</summary>
    private bool HasMapClasses
    {
        get
        {
            lock (_gate)
                return _mapClasses.Length > 0;
        }
    }

    private static int IndexOfOption(string[] options, string? current)
    {
        var trimmed = current?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return 0; // 第 0 项就是「（请选择地图模式）」
        var index = Array.IndexOf(options, trimmed);
        return index >= 0 ? index : 0;
    }

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
        LogInfo(
            $"对局快捷键已启动：快捷键一 {options.HotkeyA.DisplayName} → {DescribeClass(options.MapClassA)}；"
            + $"快捷键二 {options.HotkeyB.DisplayName} → {DescribeClass(options.MapClassB)}；"
            + $"仅游戏内生效 {((options.ForegroundOnly) ? "开" : "关")}。");
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

    // ════════════════ 设置读写 ════════════════

    public object? GetSettingValue(string key) => key switch
    {
        MatchHotkeysOptions.HotkeyAKey => _options.HotkeyA.StorageValue,
        MatchHotkeysOptions.HotkeyBKey => _options.HotkeyB.StorageValue,
        MatchHotkeysOptions.MapClassAKey => _options.MapClassA,
        MatchHotkeysOptions.MapClassBKey => _options.MapClassB,
        MatchHotkeysOptions.ForegroundOnlyKey => _options.ForegroundOnly,
        _ => null
    };

    public void SetSettingValue(string key, object? value)
    {
        var updated = BuildOptions(key, value);
        if (ReferenceEquals(updated, _options))
            return;

        _options = updated;
        // 宿主可能在 OnLoad 之前就用持久化值回填（PluginManager.Start 先恢复设置再启动生命周期）。
        if (_enabled)
            ApplyHotkeyBindings();
    }

    private MatchHotkeysOptions BuildOptions(string key, object? value)
    {
        switch (key)
        {
            case MatchHotkeysOptions.HotkeyAKey when value is string hotkeyA:
                return MatchHotkeysOptions.TryParseHotkey(hotkeyA, out var parsedA)
                    ? _options with { HotkeyA = parsedA }
                    : _options;
            case MatchHotkeysOptions.HotkeyBKey when value is string hotkeyB:
                return MatchHotkeysOptions.TryParseHotkey(hotkeyB, out var parsedB)
                    ? _options with { HotkeyB = parsedB }
                    : _options;
            case MatchHotkeysOptions.MapClassAKey when value is string mapClassA:
                return SelectClass(isSlotA: true, mapClassA);
            case MatchHotkeysOptions.MapClassBKey when value is string mapClassB:
                return SelectClass(isSlotA: false, mapClassB);
            case MatchHotkeysOptions.ForegroundOnlyKey when value is bool foregroundOnly:
                return _options with { ForegroundOnly = foregroundOnly };
            default:
                return _options;
        }
    }

    /// <summary>
    /// 写入地图模式。选中「（请选择地图模式）」＝清空该快捷键的目标；
    /// 但地图库此刻读不到内容时不清空——那种情况下这个值多半是宿主回填的默认项，
    /// 清掉会把用户之前保存的选择抹没。
    /// </summary>
    private MatchHotkeysOptions SelectClass(bool isSlotA, string value)
    {
        if (!MatchHotkeysOptions.IsUnsetClass(value))
        {
            var trimmed = value.Trim();
            return isSlotA
                ? _options with { MapClassA = trimmed }
                : _options with { MapClassB = trimmed };
        }

        if (!HasMapClasses)
            return _options;
        return isSlotA
            ? _options with { MapClassA = string.Empty }
            : _options with { MapClassB = string.Empty };
    }

    // ════════════════ 热键 ════════════════

    private void ApplyHotkeyBindings()
    {
        if (_input is not { } input)
            return;
        var options = _options;
        ApplyHotkeyBinding(input, MatchHotkeysOptions.HotkeyAKey, options.HotkeyA);
        ApplyHotkeyBinding(input, MatchHotkeysOptions.HotkeyBKey, options.HotkeyB);
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
        if (string.Equals(args.BindingKey, MatchHotkeysOptions.HotkeyAKey, StringComparison.Ordinal))
            _ = ToggleMatchAsync(slotA: true);
        else if (string.Equals(args.BindingKey, MatchHotkeysOptions.HotkeyBKey, StringComparison.Ordinal))
            _ = ToggleMatchAsync(slotA: false);
    }

    private async Task ToggleMatchAsync(bool slotA)
    {
        var options = _options;
        var slotName = slotA ? "快捷键一" : "快捷键二";
        var binding = slotA ? options.HotkeyA : options.HotkeyB;
        var target = (slotA ? options.MapClassA : options.MapClassB).Trim();

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
                // 可见反馈：与本体「对局状态开关」一样在屏幕上弹一条浮层提示，
                // 否则开局只影响运行时状态、玩家那边什么也看不到。
                Toast(warning: alreadyInTargetMatch,
                    alreadyInTargetMatch ? "已结束对局" : $"已进入对局 · {target}");
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

    // ════════════════ 地图模式列表 ════════════════

    /// <summary>
    /// 首次渲染设置描述符时同步取一次地图库。
    ///
    /// 原因：宿主启动时先用持久化值回填（<c>PluginPreferencesStore.RestoreSettings</c>），
    /// 而 <see cref="PluginChoiceSetting"/> 只有在「已存的值∈Options」时才会被采用；
    /// 若此刻下拉候选还是空的，宿主会把用户保存的地图模式回退成默认项。
    /// 这一步跑在启动的后台线程上（地图库此时已被 SessionOrchestrator 预热），
    /// 之后一切异步刷新，不再阻塞。
    /// </summary>
    private void EnsureMapClassesForFirstRender()
    {
        lock (_gate)
        {
            if (_mapClassSyncAttempted)
                return;
            _mapClassSyncAttempted = true;
        }

        // TryFetchMapClassesAsync 自身不抛异常，因此这里的超时分支不会留下未观测的任务异常。
        var fetch = TryFetchMapClassesAsync();
        if (fetch.Wait(MapClassSyncWaitMilliseconds) && fetch.IsCompletedSuccessfully)
        {
            ApplyMapClasses(fetch.Result);
            return;
        }

        RequestMapClassRefresh();
    }

    private void RequestMapClassRefresh()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _mapClassesRefreshedTick) < MapClassRefreshCooldownMilliseconds)
            return;
        if (Interlocked.CompareExchange(ref _mapClassRefreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                ApplyMapClasses(await TryFetchMapClassesAsync().ConfigureAwait(false));
            }
            finally
            {
                Volatile.Write(ref _mapClassRefreshRunning, 0);
            }
        });
    }

    /// <summary>取地图库；失败只记一条日志并返回空列表，绝不把异常抛给调用方。</summary>
    private async Task<string[]> TryFetchMapClassesAsync()
    {
        try
        {
            return await FetchMapClassesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogWarning($"读取地图库失败：{Describe(exception)}");
            return [];
        }
    }

    private async Task<string[]> FetchMapClassesAsync()
    {
        if (_matches is not { } matches)
            return [];
        var classes = await matches.GetMapClassesAsync(CancellationToken.None).ConfigureAwait(false);
        return classes
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void ApplyMapClasses(string[] classes)
    {
        lock (_gate)
            _mapClasses = classes;
        Interlocked.Exchange(ref _mapClassesRefreshedTick, Environment.TickCount64);
        if (classes.Length == 0)
            LogInfo("地图库里暂时没有可用的地图模式，下拉框只会有「（请选择地图模式）」；装好地图后打开设置页即可刷新。");
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
