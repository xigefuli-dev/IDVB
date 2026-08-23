// IDVB Remaster — Session Orchestrator 楼层切换与持久小地图

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public void SwitchFloor() => HandleSwitchFloorSafely();

    /// <summary>
    /// Selects an exact user-visible floor position. RealCLI uses this after
    /// changing overlay_game so a runtime configured to skip automatic floor
    /// recognition still aligns the floor shown by the simulator.
    /// </summary>
    public bool SelectFloorPosition(int position)
    {
        try
        {
            if (!TryGetFloorSwitchIdentity(
                    out var recognition,
                    out var identityState,
                    out var matchVersion))
            {
                return false;
            }

            var decision = MapFloorSwitchDecision.AtPosition(
                recognition.Map,
                _currentFloorKey ?? recognition.Result.Floor,
                position);
            if (!decision.Succeeded || decision.ToFloorKey is null)
            {
                ReportFloorSwitchRejected(
                    decision.Failure,
                    identityState,
                    matchVersion,
                    recognition.Map.Id,
                    decision.FromFloorKey,
                    requestedPosition: position);
                return false;
            }

            ApplyFloorSwitch(
                recognition,
                identityState,
                matchVersion,
                decision,
                source: "real-cli-position",
                requestedPosition: position);
            return true;
        }
        catch (Exception exception)
        {
            ReportFloorSwitchException(exception, "real-cli-position");
            return false;
        }
    }

    /// <summary>
    /// Keeps the CLI and the player's floor hotkey on the same floor-switch
    /// implementation.  The CLI does not perform floor recognition itself.
    /// </summary>
    private void HandleSwitchFloor()
    {
        if (!TryGetFloorSwitchIdentity(
                out var recognition,
                out var identityState,
                out var matchVersion))
        {
            return;
        }

        var decision = MapFloorSwitchDecision.Next(
            recognition.Map,
            _currentFloorKey ?? recognition.Result.Floor);
        if (!decision.Succeeded || decision.ToFloorKey is null)
        {
            ReportFloorSwitchRejected(
                decision.Failure,
                identityState,
                matchVersion,
                recognition.Map.Id,
                decision.FromFloorKey);
            return;
        }

        ApplyFloorSwitch(
            recognition,
            identityState,
            matchVersion,
            decision,
            source: "hotkey");
    }

    private void HandleSwitchFloorSafely()
    {
        try
        {
            HandleSwitchFloor();
        }
        catch (Exception exception)
        {
            ReportFloorSwitchException(exception, "hotkey");
        }
    }

    private bool TryGetFloorSwitchIdentity(
        out RuntimeMapRecognition recognition,
        out string identityState,
        out long matchVersion)
    {
        recognition = null!;
        identityState = "none";
        var match = _matchSession.Snapshot;
        matchVersion = match.Version;

        if (_disposed || !_initialized || !match.IsStarted)
        {
            _statusMessage = !match.IsStarted
                ? "请先进入对局，再切换小地图楼层。"
                : "地图运行时尚未就绪。";
            _logCollector.Append(
                MapLogCategory.FloorRecognition,
                MapLogLevel.Warning,
                $"楼层切换被拒绝：{_statusMessage}",
                details: new()
                {
                    ["outcome"] = "rejected",
                    ["reason"] = !match.IsStarted
                        ? "match-not-started"
                        : "runtime-not-ready",
                    ["matchVersion"] = matchVersion,
                    ["identityState"] = identityState
                });
            NotifyStateChanged();
            return false;
        }

        var identity = MapFloorIdentityRules.Resolve(
            _lastRecognition,
            _pendingAlignmentIdentity);
        if (identity.Identity is { } available)
        {
            recognition = available;
            identityState = identity.State switch
            {
                MapFloorIdentityState.Aligned => "aligned",
                MapFloorIdentityState.PendingAlignment => "pending-alignment",
                _ => "none"
            };
            return true;
        }

        _statusMessage = "尚未锁定地图，无法切换楼层；请先执行快捷扫描。";
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Warning,
            $"楼层切换被拒绝：{_statusMessage}",
            details: new()
            {
                ["outcome"] = "rejected",
                ["reason"] = "map-identity-unavailable",
                ["matchVersion"] = matchVersion,
                ["identityState"] = identityState
            });
        NotifyStateChanged();
        return false;
    }

    private void ApplyFloorSwitch(
        RuntimeMapRecognition recognition,
        string identityState,
        long matchVersion,
        MapFloorSwitchDecision decision,
        string source,
        int? requestedPosition = null)
    {
        var openSession = _mapOpenSession.Snapshot;
        var retargetsPendingVariant =
            openSession.State == MapSessionState.RecalibrationRequired
            && openSession.RecalibrationReason == MapRecalibrationReason.VariantChanged
            && openSession.MapId == recognition.Map.Id
            && _pendingAlignmentIdentity?.Map.Id == recognition.Map.Id;
        CancelOrbTracking("floor changed");
        SuspendActiveAdaptiveFloor("floor changed");
        var nextFloorKey = decision.ToFloorKey!;
        _currentFloorKey = nextFloorKey;
        if (retargetsPendingVariant)
        {
            // The pending identity must move with the visible floor. Keeping
            // Result.Floor on the old value made the control panel, adaptive
            // key and first successful alignment disagree until the next map
            // open event rebuilt the result.
            _pendingAlignmentIdentity = CreatePendingVariantIdentity(
                recognition.Map,
                nextFloorKey);
            _pendingAlignmentSeed = null;
            _lastFloorRecognition = null;
            ClearAdaptiveSessionKeys();
            _mapOpenSession.RetargetVariantFloor(
                recognition.Map.Id,
                nextFloorKey);
        }
        MapOverlayPresentationBatch.Apply(_overlay, () =>
        {
            if (retargetsPendingVariant)
                _overlayStatus.Clear();
            if (!string.Equals(
                    recognition.Result.Floor,
                    nextFloorKey,
                    StringComparison.Ordinal))
            {
                _overlay.ClearMap();
            }
            RefreshMiniMapForCurrentFloor();
            try { _overlay.Show(); } catch { }
        });
        var floorLabel = MapFloorRules.GetFloorDisplayName(recognition.Map, nextFloorKey);
        _statusMessage = retargetsPendingVariant && _gameMapToggleState.IsOpen
            ? $"已手动切换到{floorLabel}，正在按目标变体重新对齐。"
            : $"已手动切换到{floorLabel}；下次开图将按该楼层对齐。";
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Info,
            $"手动楼层切换：{floorLabel}",
            details: new()
            {
                ["outcome"] = "switched",
                ["source"] = source,
                ["matchVersion"] = matchVersion,
                ["identityState"] = identityState,
                ["mapId"] = recognition.Map.Id,
                ["fromFloor"] = decision.FromFloorKey,
                ["toFloor"] = nextFloorKey,
                ["requestedPosition"] = requestedPosition
            });
        NotifyStateChanged();
        if (retargetsPendingVariant && _gameMapToggleState.IsOpen)
        {
            var transition = new MapGameToggleTransition(
                IsOpen: true,
                Version: _gameMapToggleState.Version);
            StartInputOperation(
                "variant-floor-realignment",
                () => RunMapOpenAlignmentAsync(transition));
        }
    }

    private void ReportFloorSwitchRejected(
        MapFloorSwitchFailure failure,
        string identityState,
        long matchVersion,
        Guid mapId,
        string? fromFloor,
        int? requestedPosition = null)
    {
        _statusMessage = failure switch
        {
            MapFloorSwitchFailure.NoFloors => "当前地图没有可用楼层。",
            MapFloorSwitchFailure.NoOtherFloor => "当前地图只有一个楼层，无需切换。",
            MapFloorSwitchFailure.InvalidPosition => "请求的楼层位置无效。",
            _ => "当前无法切换楼层。"
        };
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Warning,
            $"楼层切换被拒绝：{_statusMessage}",
            details: new()
            {
                ["outcome"] = "rejected",
                ["reason"] = failure.ToString(),
                ["matchVersion"] = matchVersion,
                ["identityState"] = identityState,
                ["mapId"] = mapId,
                ["fromFloor"] = fromFloor,
                ["requestedPosition"] = requestedPosition
            });
        NotifyStateChanged();
    }

    private void ReportFloorSwitchException(Exception exception, string source)
    {
        _statusMessage = $"楼层切换异常：{exception.Message}";
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Error,
            _statusMessage,
            details: new()
            {
                ["outcome"] = "error",
                ["source"] = source,
                ["exceptionType"] = exception.GetType().FullName,
                ["exception"] = exception.ToString()
            });
        NotifyStateChanged();
    }

    private void RefreshMiniMapForCurrentFloor()
    {
        // A map may be confidently identified before a usable screen
        // transform is available. The persistent mini-map only needs the
        // map identity and floor image, so it must not wait for alignment.
        var lockedRecognition = _lastRecognition ?? _pendingAlignmentIdentity;
        if (!Settings.PersistentMiniMapEnabled
            || lockedRecognition?.Map is not { } map)
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }

        MapOverlayTransform? transform = null;
        string effectiveFloorKey;

        if (lockedRecognition is { } recognition
            && recognition.Result.OverlayTransform is { } existingTransform
            && string.Equals(
                recognition.Result.Floor,
                _currentFloorKey ?? recognition.Result.Floor,
                StringComparison.Ordinal))
        {
            transform = existingTransform;
            effectiveFloorKey = _currentFloorKey ?? recognition.Result.Floor;
        }
        else
        {
            effectiveFloorKey = _currentFloorKey
                ?? MapFloorRules.GetPrimaryFloorKey(map);
            var floorProfile = MapFloorRules.GetFloorProfile(map, effectiveFloorKey)
                ?? map.Recognition?.FirstFloor;
            if (floorProfile is null)
            {
                _overlay.ClearPersistentMiniMap();
                return;
            }
            transform = new MapOverlayTransform
            {
                ReferenceWidth = floorProfile.RecognitionPixelWidth,
                ReferenceHeight = floorProfile.RecognitionPixelHeight,
                ScaleX = 1.0,
                ScaleY = 1.0,
                OffsetX = 0,
                OffsetY = 0,
                ReferenceCenterX = floorProfile.RecognitionPixelWidth / 2.0,
                ReferenceCenterY = floorProfile.RecognitionPixelHeight / 2.0,
                ScreenCenterX = 0,
                ScreenCenterY = 0,
                OrientationDegrees = 0,
                AlignmentMode = Settings.OverlayAlignmentMode
            };
        }

        if (transform is null
            || MapFloorRules.GetFloorProfile(map, effectiveFloorKey) is null)
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }

        var overlayPath = _mapRepository.GetFloorOverlayPath(map, effectiveFloorKey);
        if (!File.Exists(overlayPath))
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }
        var profile = MapFloorRules.GetFloorProfile(map, effectiveFloorKey)
            ?? map.Recognition?.FirstFloor;
        if (profile is null)
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }
        var anchors = profile.Anchors
            .Where(anchor => anchor.Bounds?.IsValid is true)
            .Select(anchor => new MapOverlayRenderAnchor(
                anchor.Key,
                anchor.DisplayName,
                anchor.Bounds!.Clone()))
            .ToArray();
        var annotations = profile.Annotations
            .Where(a => a.IsValid)
            .Select(a => new MapOverlayRenderAnnotation(
                a.Type,
                a.ColorIndex,
                a.EffectiveColorHex,
                a.Bounds?.Clone(),
                a.Start?.Clone(),
                a.End?.Clone(),
                a.Text,
                a.FontFamily,
                a.FontSize,
                a.IsBold,
                a.IsItalic,
                a.IsStrikethrough))
            .ToArray();
        var floorLabel = MapFloorRules.GetFloorDisplayName(map, effectiveFloorKey);
        _overlay.SetPersistentMiniMapState(
            overlayPath,
            transform,
            _lastGameBounds,
            _lastGameWindowHandle,
            Settings.MiniMapScale,
            anchors,
            annotations,
            floorLabel);
    }

}
/*
 * 文件职责：SessionOrchestrator.Floor。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
