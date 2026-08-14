namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal sealed partial class AdaptiveScaleCoordinator
{
    public void BeginOrResumeOpen(long openId, AdaptiveScaleKey key)
    {
        lock (_stateGate)
        {
            _activeOpenId = openId;
            _activeKey = key;
        }
    }

    public bool TryGetActiveKey(out AdaptiveScaleKey key)
    {
        lock (_stateGate)
        {
            if (_activeKey is { } active
                && _controllers.TryGetValue(active, out var controller)
                && controller.IsOpen)
            {
                key = active;
                return true;
            }
            key = default;
            return false;
        }
    }

    public void EndOpen(long openId, string reason)
    {
        lock (_stateGate)
        {
            foreach (var controller in _controllers.Values)
                controller.EndOpen(openId);
            if (_activeOpenId == openId)
            {
                _activeKey = null;
                _activeOpenId = 0;
            }
        }
        _log?.Invoke(
            "adaptive map open ended",
            new Dictionary<string, object?>
            {
                ["openId"] = openId,
                ["reason"] = reason
            });
    }

    public void SuspendActiveFloor(long openId, string reason)
    {
        AdaptiveScaleKey? suspended = null;
        lock (_stateGate)
        {
            if (_activeOpenId != openId || _activeKey is not { } active)
                return;
            suspended = active;
            _activeKey = null;
        }
        _log?.Invoke(
            "adaptive floor suspended",
            new Dictionary<string, object?>
            {
                ["openId"] = openId,
                ["mapId"] = suspended.Value.MapId,
                ["floor"] = suspended.Value.FloorKey,
                ["reason"] = reason
            });
    }

    public void EndActiveOpen(string reason)
    {
        long openId;
        lock (_stateGate)
            openId = _activeOpenId;
        if (openId > 0)
            EndOpen(openId, reason);
    }

    public bool CanUseAsReliableSession(
        MapAlignmentSession session,
        AdaptiveScaleKey currentKey,
        long openId)
    {
        if (!_options.Enabled)
            return true;
        lock (_stateGate)
            return _controllers.TryGetValue(currentKey, out var controller)
                && controller.IsOpen
                && controller.OpenId == openId
                && currentKey.MapId == session.MapId
                && currentKey.MapUpdatedAtTicks == session.MapUpdatedAt.UtcTicks
                && currentKey.FloorKey == AdaptiveScaleKey.NormalizeFloor(session.FloorKey)
                && controller.CanUseReliableScale(UniformScale(session.LockedTransform));
    }

    public bool IsConfirmedTransform(
        AdaptiveScaleKey expectedKey,
        long openId,
        MapOverlayTransform transform)
    {
        if (!_options.Enabled)
            return true;
        lock (_stateGate)
            return TryGetOpenController(expectedKey, openId, out var controller)
                && controller.CanUseReliableScale(UniformScale(transform));
    }

    public bool RequiresWideScaleSearch(AdaptiveScaleKey expectedKey, long openId)
    {
        lock (_stateGate)
            return TryGetOpenController(expectedKey, openId, out var controller)
                && controller.State == AdaptiveScaleState.Recovering;
    }

    public void ObserveStructureFailure(AdaptiveScaleKey expectedKey, long openId)
    {
        lock (_stateGate)
        {
            if (TryGetOpenController(expectedKey, openId, out var controller))
                controller.ObserveStructureFailure();
        }
    }

    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_pendingWrites)
            pending = _pendingWrites.ToArray();
        foreach (var task in pending)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _log?.Invoke(
                    "adaptive scale persistence drain failed",
                    new Dictionary<string, object?>
                    {
                        ["exception"] = exception.GetBaseException().ToString()
                    });
            }
        }
    }
}
