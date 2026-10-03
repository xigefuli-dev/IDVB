using IDVBuff.Core.Models;
using IdentityVisionBridge.PluginSdk;
using IdentityVisionBridge.Vision;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private bool _pluginMatchTransitionActive;
    private readonly Dictionary<string, HostOperationResult> _pluginMatchOperations = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> GetPluginMapClassesAsync(CancellationToken token)
    {
        var classes = await GetMapClassesAsync();
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(classes.ToArray());
    }

    public async Task<HostOperationResult> QueuePluginMatchAsync(HostMatchRequest? request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !_initialized || _settings is null)
            return PluginControlResult(HostOperationState.NotReady, "宿主尚未就绪。");
        if (_pluginMatchTransitionActive)
            return PluginControlResult(HostOperationState.Busy, "正在切换对局。");
        if (request is not null)
        {
            var classes = await GetPluginMapClassesAsync(token);
            if (!classes.Contains(request.MapClass, StringComparer.Ordinal))
                return PluginControlResult(HostOperationState.Rejected, $"没有此模式：{request.MapClass}");
            if (IsMatchStarted && !request.EndCurrentMatch)
                return PluginControlResult(HostOperationState.Rejected, "已有对局正在进行。");
        }
        token.ThrowIfCancellationRequested();
        // The catalog read yielded the dispatcher: another request may have won the race.
        if (_pluginMatchTransitionActive)
            return PluginControlResult(HostOperationState.Busy, "正在切换对局。");
        var id = Guid.NewGuid().ToString("N");
        var accepted = new HostOperationResult(id, HostOperationState.Accepted, "对局操作已交由宿主执行。");
        _pluginMatchTransitionActive = true;
        while (_pluginMatchOperations.Count >= 64)
            _pluginMatchOperations.Remove(_pluginMatchOperations.Keys.First());
        _pluginMatchOperations[id] = accepted;
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Ownership transfers at acceptance. Ending a match must not cancel its own begin operation.
                await EnsureMapCacheSynchronizedAsync();
                await _matchLifecycleGate.WaitAsync();
                try
                {
                    if (request is not null)
                    {
                        var classes = await GetMapClassesAsync();
                        if (!classes.Contains(request.MapClass, StringComparer.Ordinal))
                            throw new InvalidOperationException($"模式已从目录移除：{request.MapClass}");
                        if (IsMatchStarted && !request.EndCurrentMatch)
                            throw new InvalidOperationException("已有对局正在进行。");
                    }
                    await EndMatchCoreAsync(saveAutomaticMapCache: false);
                    if (request is not null)
                    {
                        ObjectDisposedException.ThrowIf(_disposed, this);
                        await SetLastSelectedMapClassAsync(request.MapClass);
                        await BeginMatchCoreAsync(request.MapClass);
                        if (!IsMatchStarted || !string.Equals(MatchSnapshot.MapClass, request.MapClass, StringComparison.Ordinal))
                            throw new InvalidOperationException("对局状态未完成提交。");
                    }
                    _pluginMatchOperations[id] = new(id, HostOperationState.Applied,
                        request is null ? "对局已结束。" : $"对局已开始 · {request.MapClass}") { Snapshot = GetPluginSnapshot() };
                }
                finally { _matchLifecycleGate.Release(); }
            }
            catch (Exception exception)
            {
                _pluginMatchOperations[id] = new(id, HostOperationState.Failed, exception.GetBaseException().Message);
                _logCollector.Append(MapLogCategory.Session, MapLogLevel.Error,
                    $"插件对局操作失败 · {id} · {exception.GetBaseException().Message}");
            }
            finally { _pluginMatchTransitionActive = false; StateChanged?.Invoke(this, EventArgs.Empty); }
        }))
        {
            _pluginMatchTransitionActive = false;
            return _pluginMatchOperations[id] = new(id, HostOperationState.Failed, "宿主调度器不可用。");
        }
        return accepted;
    }

    public HostOperationResult? GetPluginMatchOperation(string id) => _pluginMatchOperations.GetValueOrDefault(id);

    private HostOperationResult PluginControlResult(HostOperationState state, string message) =>
        new(Guid.NewGuid().ToString("N"), state, message) { Snapshot = GetPluginSnapshot() };

    private bool PluginControlReady => !_disposed && _initialized && _settings is not null;
    private bool PluginControlBusy => _pluginMatchTransitionActive || _pluginOperationActive || IsScanning || _scanGate.CurrentCount == 0;

    public async Task<HostOperationResult> SelectPluginMapAsync(Guid mapId, string floorKey, CancellationToken token)
    {
        if (!PluginControlReady || !IsMatchStarted)
            return PluginControlResult(HostOperationState.NotReady, "请先开始对局。");
        if (PluginControlBusy) return PluginControlResult(HostOperationState.Busy, "宿主正在执行操作。");
        var matchVersion = MatchSnapshot.Version;
        await EnsureMapCacheSynchronizedAsync();
        token.ThrowIfCancellationRequested();
        if (PluginControlBusy || !IsMatchStarted || MatchSnapshot.Version != matchVersion)
            return PluginControlResult(HostOperationState.Busy, "对局或操作状态已变化。");
        var map = _recognition.TryGetMap(mapId);
        var floor = map?.Floors.FirstOrDefault(value => string.Equals(value.Key, floorKey, StringComparison.OrdinalIgnoreCase));
        if (map is null || floor is null || !string.Equals(map.Class, MatchSnapshot.MapClass, StringComparison.Ordinal))
            return PluginControlResult(HostOperationState.Rejected, "地图、楼层或当前模式不匹配。");
        var bounds = _lastGameBounds;
        var window = _lastGameWindowHandle;
        if (_captureSvc.TryGetForegroundClientBounds(out var capturedBounds, out var capturedWindow, out _)
            && capturedBounds is MapScreenRect actualBounds)
        { bounds = actualBounds; window = capturedWindow; }
        UnlockMapForRescan();
        LockSelectedMapIdentity(new RuntimeMapRecognition
        {
            Map = map, Result = new() { MapId = map.Id, Floor = floor.Key, Confidence = 1, IdentityConfidence = 1 }
        }, bounds, window, userConfirmed: true);
        return PluginControlResult(HostOperationState.Applied, "地图身份已锁定，等待结构对齐。");
    }

    public HostOperationResult SelectPluginFloor(string floorKey, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!PluginControlReady || !IsMatchStarted)
            return PluginControlResult(HostOperationState.NotReady, "请先开始对局。");
        if (PluginControlBusy) return PluginControlResult(HostOperationState.Busy, "宿主正在执行操作。");
        var identity = LastRecognition ?? PendingAlignmentIdentity;
        var floor = identity?.Map.Floors.FirstOrDefault(value => string.Equals(value.Key, floorKey, StringComparison.OrdinalIgnoreCase));
        if (identity is null || floor is null)
            return PluginControlResult(HostOperationState.Rejected, "当前地图没有此楼层。");
        var applied = SelectFloorPosition(MapFloorRules.GetFloorPosition(identity.Map, floor.Key));
        return PluginControlResult(applied ? HostOperationState.Applied : HostOperationState.Rejected, StatusMessage);
    }

    public HostOperationResult SetPluginOverlayVisible(bool visible, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!PluginControlReady) return PluginControlResult(HostOperationState.NotReady, "宿主尚未就绪。");
        if (PluginControlBusy) return PluginControlResult(HostOperationState.Busy, "宿主正在执行操作。");
        if (_overlay.IsVisible != visible) ToggleOverlay();
        return PluginControlResult(_overlay.IsVisible == visible ? HostOperationState.Applied : HostOperationState.Rejected, StatusMessage);
    }

    public HostSettingsSnapshot GetPluginSettings()
    {
        if (!PluginControlReady) throw new InvalidOperationException("宿主尚未就绪。");
        return new(_settings!.IsEnabled, (VisionScanMode)_settings.ScanPerformanceMode,
            _settings.DisableAutoFloor, _settings.MapOpacity, _settings.MiniMapScale);
    }

    public async Task<HostOperationResult> UpdatePluginSettingsAsync(HostSettingsPatch patch, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(patch);
        token.ThrowIfCancellationRequested();
        if (!PluginControlReady) return PluginControlResult(HostOperationState.NotReady, "宿主尚未就绪。");
        if (PluginControlBusy) return PluginControlResult(HostOperationState.Busy, "宿主正在执行操作。");
        if ((patch.ScanMode is { } mode && !Enum.IsDefined(mode))
            || (patch.MapOpacity is { } opacity && (!double.IsFinite(opacity) || opacity < 0 || opacity > 1))
            || (patch.MiniMapScale is { } scale && (!double.IsFinite(scale) || scale < 0 || scale > 1)))
            return PluginControlResult(HostOperationState.Rejected, "设置参数超出有效范围。");
        _pluginOperationActive = true;
        try
        {
            if (patch.ScanMode is { } scanMode) { token.ThrowIfCancellationRequested(); await SetScanPerformanceModeAsync((ScanPerformanceMode)scanMode); }
            if (patch.DisableAutoFloor is { } disable) { token.ThrowIfCancellationRequested(); await SetDisableAutoFloorAsync(disable); }
            if (patch.MapOpacity is { } mapOpacity) { token.ThrowIfCancellationRequested(); await SetMapOpacityAsync(mapOpacity); }
            if (patch.MiniMapScale is { } miniScale) { token.ThrowIfCancellationRequested(); await SetMiniMapScaleAsync(miniScale); }
            if (patch.IsEnabled is { } enabled) { token.ThrowIfCancellationRequested(); await SetEnabledAsync(enabled); }
            return PluginControlResult(HostOperationState.Applied, "设置已保存并应用。");
        }
        finally { _pluginOperationActive = false; }
    }
}
