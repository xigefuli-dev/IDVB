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
            CancelOrbTracking("game map closed");
            _overlay.ClearMap();
            RefreshMiniMapForCurrentFloor();
            try { _overlay.Show(); } catch { }
            if (_matchSession.Snapshot.Mode == MapRunMode.Survey)
            {
                await HandleSurveyMapClosedAsync();
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        if (_matchSession.Snapshot.Mode == MapRunMode.Survey)
            await HandleSurveyMapOpenAsync(toggle);
        else
            await RunMapOpenAlignmentAsync(toggle);
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
        var profile = map.Recognition.FirstFloor;
        var main = profile.FindAnchor("main-entrance");
        var side = profile.FindAnchor("side-entrance");
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
