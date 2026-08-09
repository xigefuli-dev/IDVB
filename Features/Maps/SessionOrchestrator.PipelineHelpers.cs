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

        await TryAutoMatchResolutionPresetAsync(clientBounds);
        var toggle = _gameMapToggleState.Toggle();
        if (!toggle.IsOpen)
        {
            _overlay.ClearMap();
            RefreshMiniMapForCurrentFloor();
            try { _overlay.Show(); } catch { }
            return;
        }

        await RunMapOpenAlignmentAsync(toggle);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task TryAutoMatchResolutionPresetAsync(
        MapScreenRect clientBounds)
    {
        var profiles = GetAvailablePresets();
        if (profiles.Count == 0 || !clientBounds.IsValid)
            return;

        const int dpi = 120;
        var profile = profiles.FirstOrDefault(candidate =>
                candidate.ClientWidth == (int)Math.Round(clientBounds.Width)
                && candidate.ClientHeight == (int)Math.Round(clientBounds.Height)
                && candidate.Dpi == dpi)
            ?? profiles
                .Where(candidate => candidate.Dpi == dpi)
                .OrderBy(candidate =>
                    Math.Abs(candidate.ClientWidth - clientBounds.Width)
                    + Math.Abs(candidate.ClientHeight - clientBounds.Height))
                .FirstOrDefault(candidate =>
                    Math.Abs(candidate.ClientWidth - clientBounds.Width) <= 100
                    && Math.Abs(candidate.ClientHeight - clientBounds.Height) <= 100);
        if (profile is null)
            return;

        var activeGeometry = _config.ActiveResolutionPreset.Split(' ')[0];
        var profileGeometry = profile.Name.Split(' ')[0];
        if (string.Equals(
            activeGeometry,
            profileGeometry,
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await SetActivePresetAsync(profile.Name);
        _logCollector.Append(
            MapLogCategory.System,
            MapLogLevel.Info,
            $"已按游戏客户区自动匹配分辨率预设 · {profile.Name}",
            details: new()
            {
                ["clientWidth"] = clientBounds.Width,
                ["clientHeight"] = clientBounds.Height,
                ["preset"] = profile.Name
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
