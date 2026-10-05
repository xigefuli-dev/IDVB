using IDVBuff.Features.Notifications;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunSilentScanAsync(MapGameToggleTransition toggle)
    {
        // Tag selection is intrinsically manual and cannot yield a silent identity.
        if (_settings?.SelectMapByTagsEnabled == true)
            return;
        if (_silentScanActive)
            return;

        var operationMatch = _matchSession.Snapshot;
        var previousStatus = _statusMessage;
        _silentScanActive = true;
        try
        {
            await RunRecognitionPipelineAsync();
        }
        catch (Exception ex)
        {
            _logCollector.Append(MapLogCategory.ScanLifecycle,
                MapLogLevel.Error, $"静默扫描异常：{ex}");
        }
        finally
        {
            _silentScanActive = false;
            _statusMessage = previousStatus;
        }

        if (!IsCurrentMatchOperation(operationMatch)
            || _backgroundScanStatus != BackgroundScanStatus.CompletedIdentified
            || _pendingBackgroundIdentity is not { } identity)
        {
            ClearPendingBackgroundScan();
            return;
        }

        // Identity and alignment have separate lifetimes. A close during the
        // scan retains the identity and lets the next open perform alignment.
        var floor = ResolveBackgroundConsumeFloorKey(identity);
        _pendingAlignmentIdentity = identity;
        _currentFloorKey = floor;
        _mapLease.Bind(operationMatch, identity.Map.Id);
        _pendingAlignmentSeed = BackgroundScanRules.PickSideEntranceSeed(
                _pendingBackgroundSeed, identity, floor)
            ?? CreateIndependentFloorSeedSession(identity, floor);
        _mapOpenSession.LockMapIdentity(
            identity.Map.Id, floor, identity.Result.IdentityConfidence);
        _lastRecognition = identity;
        QueueVariantIdentityNotification(identity.Map.Id, userConfirmed: false);
        ClearPendingBackgroundScan();
        RefreshMiniMapForCurrentFloor();
        _statusMessage = $"已确定地图：{identity.Map.DisplayName}";
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (!_headless)
            OverlayNotificationCenter.Notice(_statusMessage);

        if (_gameMapToggleState.IsOpen)
        {
            var currentOpen = new MapGameToggleTransition(
                IsOpen: true, Version: _gameMapToggleState.Version);
            await RunMapOpenAlignmentAsync(currentOpen);
        }
    }

    /// <summary>静默扫描已移除；兼容调用只能确保它保持关闭。</summary>
    public async Task SetSilentScanEnabledAsync(bool enabled)
    {
        _settings!.SilentScanEnabled = false;
        _settings.BackgroundScanEnabled = false;
        ClearPendingBackgroundScan();
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
