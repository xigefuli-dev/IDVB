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
        CancelOrbTracking("floor changed");
        var nextFloorKey = decision.ToFloorKey!;
        _currentFloorKey = nextFloorKey;
        if (!string.Equals(
                recognition.Result.Floor,
                nextFloorKey,
                StringComparison.Ordinal))
        {
            _overlay.ClearMap();
        }
        RefreshMiniMapForCurrentFloor();
        var floorLabel = MapFloorRules.GetFloorDisplayName(recognition.Map, nextFloorKey);
        _statusMessage = $"已手动切换到{floorLabel}；下次开图将按该楼层对齐。";
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
        _overlay.Show();
        NotifyStateChanged();
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
