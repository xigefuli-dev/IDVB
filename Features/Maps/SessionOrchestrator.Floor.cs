// IDVB Remaster — Session Orchestrator 楼层切换与持久小地图

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public void SwitchFloor() => HandleSwitchFloor();

    /// <summary>
    /// Selects an exact user-visible floor position. RealCLI uses this after
    /// changing overlay_game so a runtime configured to skip automatic floor
    /// recognition still aligns the floor shown by the simulator.
    /// </summary>
    public bool SelectFloorPosition(int position)
    {
        if (position < 1 || _lastRecognition is not { } recognition)
            return false;

        var floorKey = MapFloorRules.GetFloorKeyAtPosition(
            recognition.Map,
            position);
        if (floorKey is null)
            return false;

        _currentFloorKey = floorKey;
        if (!string.Equals(
                recognition.Result.Floor,
                floorKey,
                StringComparison.Ordinal))
        {
            _overlay.ClearMap();
        }
        RefreshMiniMapForCurrentFloor();
        var floorLabel = MapFloorRules.GetFloorDisplayName(
            recognition.Map,
            floorKey);
        _statusMessage = $"已手动切换到{floorLabel}；下次开图将按该楼层对齐。";
        _logCollector.Append(
            MapLogCategory.FloorRecognition,
            MapLogLevel.Info,
            $"RealCLI 楼层同步：{floorLabel}",
            details: new()
            {
                ["floor"] = floorKey,
                ["position"] = position
            });
        try { _overlay.Show(); } catch { }
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Keeps the CLI and the player's floor hotkey on the same floor-switch
    /// implementation.  The CLI does not perform floor recognition itself.
    /// </summary>
    private void HandleSwitchFloor()
    {
        if (_lastRecognition is not { } recognition)
            return;
        var nextFloorKey = MapFloorRules.GetNextFloorKey(
            recognition.Map, _currentFloorKey ?? recognition.Result.Floor);
        if (nextFloorKey is null)
            return;
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
            details: new() { ["floor"] = nextFloorKey });
        try { _overlay.Show(); } catch { }
        NotifyStateChanged();
    }

    private void RefreshMiniMapForCurrentFloor()
    {
        if (!Settings.PersistentMiniMapEnabled
            || _lastRecognition?.Map is not { } map)
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }

        MapOverlayTransform? transform = null;
        string effectiveFloorKey;

        if (_lastRecognition is { } recognition
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
                a.Bounds.Clone(),
                a.Text))
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
