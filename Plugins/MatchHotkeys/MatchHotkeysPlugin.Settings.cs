using IDVBuff.PluginContracts;
using IdentityVisionBridge.PluginSdk;

namespace IDVBuff.Plugins.MatchHotkeys;

public sealed partial class MatchHotkeysPlugin
{
    // ════════════════ 设置描述符 ════════════════

    /// <summary>
    /// 每次访问都重建描述符，并且每次重建前都重新取一次地图库：
    /// 用户新装 / 新订阅 / 删除地图类之后，只要再打开一次插件设置就能看到最新清单，
    /// 不用重启程序。
    /// </summary>
    public IReadOnlyList<IPluginSetting> Settings => BuildSettings();

    private IReadOnlyList<IPluginSetting> BuildSettings()
    {
        RefreshMapClassesForSettings();
        var options = _options;
        var settings = new List<IPluginSetting>((MatchHotkeysOptions.SlotCount * 2) + 1);

        // 各快捷键收进「按键」折叠区（默认收起），名字里不带任何括号说明。
        // 宿主按「连续且 Group 相同」把设置项折进同一个折叠区，所以这两段必须各自连续。
        for (var slot = 0; slot < MatchHotkeysOptions.SlotCount; slot++)
        {
            settings.Add(new PluginKeyBindingSetting
            {
                Key = MatchHotkeysOptions.HotkeyKeyAt(slot),
                DisplayName = MatchHotkeysOptions.SlotNameAt(slot),
                Group = MatchHotkeysOptions.KeyGroupTitle,
                DefaultValue = MatchHotkeysOptions.NoBindingStorageValue,
                AllowedKinds = PluginInputBindingKinds.All
            });
        }

        // 各目标收进「目标」折叠区（默认收起）。用不到的槽位保持「（请选择地图模式）」即可。
        for (var slot = 0; slot < MatchHotkeysOptions.SlotCount; slot++)
        {
            var mapClass = options.MapClassAt(slot);
            var classOptions = ClassOptionsFor(mapClass);
            settings.Add(new PluginChoiceSetting
            {
                Key = MatchHotkeysOptions.MapClassKeyAt(slot),
                DisplayName = MatchHotkeysOptions.TargetNameAt(slot),
                Description = "按一下进入，再按一下结束。",
                Group = MatchHotkeysOptions.TargetGroupTitle,
                Options = classOptions,
                DefaultIndex = IndexOfOption(classOptions, mapClass)
            });
        }

        // 未分组的项会被宿主平铺在设置页最上面（折叠区之上）。
        settings.Add(new PluginToggleSetting
        {
            Key = MatchHotkeysOptions.ForegroundOnlyKey,
            DisplayName = "仅在游戏窗口前台时生效",
            Description = "非游戏前台时按键无效。",
            DefaultValue = true
        });
        return settings;
    }

    /// <summary>
    /// 下拉候选＝「（请选择地图模式）」＋地图库里的全部模式，一个不少、全部可选。
    ///
    /// 已经不在当前地图库里的旧选择**不再追加**到列表里：那样会让失效目标看起来仍然可选，
    /// 选中后按快捷键只会拿到宿主的「没有此模式」。失效值由
    /// <see cref="DropStaleTargets"/> 主动清空。
    ///
    /// 唯一的例外是「地图库这次没读到」（候选为空）：此时把当前值临时留在列表末尾，
    /// 免得宿主把用户保存的选择回退掉。
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

        if (classes.Length == 0)
        {
            var trimmed = current?.Trim();
            if (!string.IsNullOrEmpty(trimmed)
                && !list.Contains(trimmed, StringComparer.Ordinal))
            {
                list.Add(trimmed);
            }
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

    public object? GetSettingValue(string key)
    {
        var hotkeySlot = MatchHotkeysOptions.SlotOfHotkeyKey(key);
        if (hotkeySlot >= 0)
            return _options.HotkeyAt(hotkeySlot).StorageValue;

        var classSlot = MatchHotkeysOptions.SlotOfMapClassKey(key);
        if (classSlot >= 0)
            return _options.MapClassAt(classSlot);

        return string.Equals(key, MatchHotkeysOptions.ForegroundOnlyKey, StringComparison.Ordinal)
            ? _options.ForegroundOnly
            : null;
    }

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
        var hotkeySlot = MatchHotkeysOptions.SlotOfHotkeyKey(key);
        if (hotkeySlot >= 0)
        {
            return value is string hotkey && MatchHotkeysOptions.TryParseHotkey(hotkey, out var parsed)
                ? _options.WithHotkey(hotkeySlot, parsed)
                : _options;
        }

        var classSlot = MatchHotkeysOptions.SlotOfMapClassKey(key);
        if (classSlot >= 0)
            return value is string mapClass ? SelectClass(classSlot, mapClass) : _options;

        return string.Equals(key, MatchHotkeysOptions.ForegroundOnlyKey, StringComparison.Ordinal)
            && value is bool foregroundOnly
                ? _options with { ForegroundOnly = foregroundOnly }
                : _options;
    }

    /// <summary>
    /// 写入地图模式。选中「（请选择地图模式）」＝清空该快捷键的目标；
    /// 但地图库此刻读不到内容时不清空——那种情况下这个值多半是宿主回填的默认项，
    /// 清掉会把用户之前保存的选择抹没。
    /// </summary>
    private MatchHotkeysOptions SelectClass(int slot, string value)
    {
        if (!MatchHotkeysOptions.IsUnsetClass(value))
            return _options.WithMapClass(slot, value.Trim());

        if (!HasMapClasses)
            return _options;
        return _options.WithMapClass(slot, string.Empty);
    }

    // ════════════════ 地图模式列表 ════════════════

    /// <summary>
    /// 每次要渲染设置描述符时都重新取一次地图库。
    ///
    /// 首次的情形：宿主启动时先用持久化值回填（<c>PluginPreferencesStore.RestoreSettings</c>），
    /// 而 <see cref="PluginChoiceSetting"/> 只有在「已存的值∈Options」时才会被采用；
    /// 若此刻下拉候选还是空的，宿主会把用户保存的地图模式回退成默认项。那一步跑在
    /// 启动的后台线程上（地图库此时已被 SessionOrchestrator 预热），所以首次多等一会儿。
    ///
    /// 之后每次打开插件设置都会再取一次：宿主渲染设置页时会连续读若干次
    /// <see cref="Settings"/>，同一批渲染内用很短的时间窗复用上次结果，避免重复读目录；
    /// 隔开一段时间再打开，就一定会去读最新地图库。
    /// </summary>
    private void RefreshMapClassesForSettings()
    {
        var firstTime = false;
        lock (_gate)
        {
            if (!_mapClassSyncAttempted)
            {
                _mapClassSyncAttempted = true;
                firstTime = true;
            }
        }

        if (!firstTime)
        {
            var now = Environment.TickCount64;
            lock (_gate)
            {
                if (_mapClasses.Length > 0
                    && now - _mapClassesRefreshedTick < MapClassSettingsRefreshWindowMilliseconds)
                {
                    return;
                }
            }

            // 后台刷新正在跑：先用现有缓存，等它落地后下一次渲染自然是新的。
            if (Volatile.Read(ref _mapClassRefreshRunning) != 0)
                return;
        }

        // TryFetchMapClassesAsync 自身不抛异常，因此这里的超时分支不会留下未观测的任务异常。
        var fetch = TryFetchMapClassesAsync();
        var wait = firstTime
            ? MapClassSyncWaitMilliseconds
            : MapClassSettingsSyncWaitMilliseconds;
        if (fetch.Wait(wait) && fetch.IsCompletedSuccessfully)
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
        {
            LogInfo("地图库里暂时没有可用的地图模式，下拉框只会有「（请选择地图模式）」；装好地图后打开设置页即可刷新。");
            return;
        }

        DropStaleTargets(classes);
    }

    /// <summary>
    /// 把还指向「已经不在当前地图库里」的模式的槽位清空。
    ///
    /// 会在两种情况下需要：用户删掉了地图类，或者地图包/订阅被整体更新替换
    /// （旧类被移除、新类换名）。留下来的旧值在下拉里既选不到，按快捷键也只会得到
    /// 宿主的「没有此模式」，所以这里直接清成「（请选择地图模式）」，让用户在设置里重选。
    ///
    /// 只在「确实读到了非空地图库」时执行；地图库暂时读不到时一律不动，
    /// 免得把用户保存的选择误清。
    /// </summary>
    private void DropStaleTargets(string[] classes)
    {
        var options = _options;
        var changed = false;
        for (var slot = 0; slot < MatchHotkeysOptions.SlotCount; slot++)
        {
            var current = options.MapClassAt(slot);
            if (MatchHotkeysOptions.IsUnsetClass(current))
                continue;

            var trimmed = current.Trim();
            if (classes.Contains(trimmed, StringComparer.Ordinal))
                continue;

            LogWarning(
                $"{MatchHotkeysOptions.TargetNameAt(slot)}的目标「{trimmed}」已不在地图库里"
                + "（可能被删除，或被地图包更新替换），已清空，请在插件设置里重新选择。");
            options = options.WithMapClass(slot, string.Empty);
            changed = true;
        }

        if (changed)
            _options = options;
    }

    /// <summary>
    /// 判断某个模式是否还在当前地图库。按快捷键前用它挡下失效目标，
    /// 给出一条能照做的提示，而不是把宿主的「没有此模式」原样抛给用户。
    ///
    /// 地图库没读到（空列表）或这一轮取不到时一律返回 true：那种情况下没有依据判定失效，
    /// 交由宿主裁决。
    /// </summary>
    private bool IsTargetInCatalog(string target)
    {
        string[] known;
        lock (_gate)
            known = _mapClasses;
        if (known.Length == 0 || known.Contains(target, StringComparer.Ordinal))
            return true;

        // 可能是刚补进地图库、插件这边还没刷新：同步再取一次再下结论。
        var fetch = TryFetchMapClassesAsync();
        if (fetch.Wait(MapClassSettingsSyncWaitMilliseconds) && fetch.IsCompletedSuccessfully)
        {
            ApplyMapClasses(fetch.Result);
            lock (_gate)
                known = _mapClasses;
            return known.Length == 0 || known.Contains(target, StringComparer.Ordinal);
        }

        return true;
    }

}
