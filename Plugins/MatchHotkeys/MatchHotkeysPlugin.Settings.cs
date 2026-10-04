using IDVBuff.PluginContracts;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Plugins.MatchHotkeys;

public sealed partial class MatchHotkeysPlugin
{
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

}
