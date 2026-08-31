namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task RebuildSideEntranceFeaturesAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var maps = await _mapRepo.GetMapsAsync();
        var total = maps.Count;
        var done = 0;
        foreach (var _ in maps)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((++done, total));
        }
    }

    private async Task HandleGameMapToggleAsync()
    {
        if (_disposed || !_settings!.IsEnabled
            || !_matchSession.Snapshot.IsStarted)
            return;
        if (!_captureSvc.TryGetForegroundClientBounds(
                out var clientBoundsObj, out _, out _)
            || clientBoundsObj is not MapScreenRect clientBounds)
            return;

        await ApplySelectedResolutionPresetAsync(clientBounds);
        var toggle = _gameMapToggleState.Toggle();
        if (!toggle.IsOpen)
        {
            await EndMapDisplayAsync("game map closed");
            return;
        }

        if (_matchSession.Snapshot.Mode == MapRunMode.Survey)
            await HandleSurveyMapOpenAsync(toggle);
        else if (_backgroundScanStatus == BackgroundScanStatus.CompletedFailed)
        {
            // 后台扫描失败：无身份可消费，提示后走标准「尚未锁定地图」路径，
            // 保证玩家手动扫描仍可正常对齐。
            _statusMessage = "后台扫描未识别出地图，请重新按快捷扫描键。";
            ClearPendingBackgroundScan();
            await RunMapOpenAlignmentAsync(toggle);
        }
        else if (IsBackgroundScanCompleted)
            await ConsumeBackgroundScanAsync(toggle);
        else
            await RunMapOpenAlignmentAsync(toggle);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Forces the runtime into the closed-map state and rejects alignment
    /// evidence for the current floor. Map identity remains locked so the next
    /// open independently realigns the same map instead of rescanning it.
    /// </summary>
    private async Task RestMapDisplayAsync()
    {
        if (_disposed || !_settings!.IsEnabled || !_matchSession.Snapshot.IsStarted)
            return;

        var identity = _pendingAlignmentIdentity ?? _lastRecognition;
        var floorKey = identity is null
            ? null
            : _currentFloorKey ?? identity.Result.Floor
                ?? MapFloorRules.GetPrimaryFloorKey(identity.Map);
        _gameMapToggleState.SetOpenForExternalController(false);
        await EndMapDisplayAsync("manual REST requested");
        if (identity is not null && !string.IsNullOrWhiteSpace(floorKey))
            await ResetLockedMapAlignmentEvidenceAsync(identity, floorKey);
    }

    private async Task EndMapDisplayAsync(string reason)
    {
        CancelMapOpenAlignment();
        EndAdaptiveMapOpen(reason);
        CancelOrbTracking(reason);
        MapOverlayPresentationBatch.Apply(_overlay, () =>
        {
            _overlayStatus.Clear();
            _overlay.ClearMap();
            RefreshMiniMapForCurrentFloor();
            try { _overlay.Show(); } catch { }
        });
        if (_matchSession.Snapshot.Mode == MapRunMode.Survey)
            await HandleSurveyMapClosedAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 对局控件激活时解析生效预设：用户指定配置优先，否则按窗口自动匹配。
    /// 仅在选择（或自动匹配）结果与当前活跃预设几何不同时才触发重载，
    /// 因此用户切换配置在「下次进入对局」生效。
    /// </summary>
    private async Task ApplySelectedResolutionPresetAsync(
        MapScreenRect clientBounds)
    {
        if (!clientBounds.IsValid)
            return;

        var width = (int)Math.Round(clientBounds.Width);
        var height = (int)Math.Round(clientBounds.Height);
        var target = ResolutionPresetResolver.ResolveEffectivePreset(
            _settings?.SelectedResolutionPreset,
            GetAvailablePresets(),
            width,
            height,
            dpi: 120);

        if (string.IsNullOrWhiteSpace(target))
            return;

        var activeGeometry = _config.ActiveResolutionPreset.Split(' ')[0];
        var targetGeometry = target.Split(' ')[0];
        if (string.Equals(
            activeGeometry,
            targetGeometry,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await SetActivePresetAsync(target);
        _logCollector.Append(
            MapLogCategory.System,
            MapLogLevel.Info,
            $"已应用分辨率预设 · {target}",
            details: new()
            {
                ["clientWidth"] = width,
                ["clientHeight"] = height,
                ["preset"] = target,
                ["selected"] = _settings?.SelectedResolutionPreset ?? "自动"
            });
    }

    private static MapGeometryFingerprint? BuildFingerprint(MapRecord map)
    {
        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        var profile = MapFloorRules.GetFloorProfile(map, floorKey)
            ?? map.Recognition.FirstFloor;
        var anchors = MapScanFloorRules.GetGeometryAnchors(map, floorKey);
        var main = anchors?.Main;
        var side = anchors?.Side;
        if (main?.Bounds?.IsValid is not true
            || side?.Bounds?.IsValid is not true
            || profile.RecognitionPixelWidth <= 0
            || profile.RecognitionPixelHeight <= 0)
        {
            return null;
        }

        return new MapGeometryFingerprint
        {
            Map = map,
            FloorKey = floorKey,
            MainPoint = new MapNormalizedPoint(
                main.Bounds.X + main.Bounds.Width / 2d,
                main.Bounds.Y + main.Bounds.Height / 2d),
            SidePoint = new MapNormalizedPoint(
                side.Bounds.X + side.Bounds.Width / 2d,
                side.Bounds.Y + side.Bounds.Height / 2d),
            MainReferenceBounds = new MapScreenRect(
                main.Bounds.X * profile.RecognitionPixelWidth,
                main.Bounds.Y * profile.RecognitionPixelHeight,
                main.Bounds.Width * profile.RecognitionPixelWidth,
                main.Bounds.Height * profile.RecognitionPixelHeight),
            SideReferenceBounds = new MapScreenRect(
                side.Bounds.X * profile.RecognitionPixelWidth,
                side.Bounds.Y * profile.RecognitionPixelHeight,
                side.Bounds.Width * profile.RecognitionPixelWidth,
                side.Bounds.Height * profile.RecognitionPixelHeight),
            ReferenceWidth = profile.RecognitionPixelWidth,
            ReferenceHeight = profile.RecognitionPixelHeight
        };
    }
}
/*
 * 文件职责：SessionOrchestrator.PipelineHelpers。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
