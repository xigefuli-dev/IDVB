using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps;

public sealed class MapOverlayStatusCoordinator : IDisposable
{
    public static readonly TimeSpan DefaultTransientLifetime = TimeSpan.FromSeconds(3);

    private readonly IOverlayWindow _overlay;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _transientLifetime;
    private readonly object _gate = new();
    private CancellationTokenSource? _expiration;
    private long _version;
    private bool _disposed;

    public MapOverlayStatusCoordinator(
        IOverlayWindow overlay,
        Action<Action> dispatch,
        TimeProvider? timeProvider = null,
        TimeSpan? transientLifetime = null)
    {
        _overlay = overlay;
        _dispatch = dispatch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transientLifetime = transientLifetime ?? DefaultTransientLifetime;
    }

    public void Show(
        MapOverlayStatus status,
        MapScreenRect gameBounds,
        IntPtr gameWindowHandle,
        bool showStatusPreference,
        bool transient)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource? expiration = null;
        long version;
        lock (_gate)
        {
            _expiration?.Cancel();
            _expiration?.Dispose();
            _expiration = transient ? new CancellationTokenSource() : null;
            expiration = _expiration;
            version = ++_version;
        }

        _overlay.UpdateStatus(
            status,
            gameBounds,
            gameWindowHandle,
            showStatusPreference,
            showImmediately: true);
        if (expiration is not null)
            _ = ExpireAsync(version, expiration);
    }

    public void Clear()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            ++_version;
            _expiration?.Cancel();
            _expiration?.Dispose();
            _expiration = null;
        }
        _overlay.ClearStatus();
    }

    private async Task ExpireAsync(long version, CancellationTokenSource expiration)
    {
        try
        {
            await Task.Delay(_transientLifetime, _timeProvider, expiration.Token);
            _dispatch(() => ClearIfCurrent(version, expiration));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearIfCurrent(long version, CancellationTokenSource expiration)
    {
        lock (_gate)
        {
            if (_disposed
                || version != _version
                || !ReferenceEquals(_expiration, expiration))
            {
                return;
            }
            _expiration = null;
            ++_version;
        }
        expiration.Dispose();
        _overlay.ClearStatus();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            ++_version;
            _expiration?.Cancel();
            _expiration?.Dispose();
            _expiration = null;
        }
    }
}

public static class MapAlignmentStatusText
{
    public static string Describe(
        RuntimeMapRecognition recognition,
        MapScanDiagnostics? diagnostics = null)
    {
        if (recognition.Result.UsedCachedScale)
            return "缓存缩放＋位置对齐";
        if (recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.DualGate)
            return "双门对齐";
        if (recognition.Result.Source is MapRecognitionSource.SideEntranceSelection
                or MapRecognitionSource.SingleGateTracking
            || recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.SingleGateAndAuxiliary)
        {
            return "单门/侧门特征对齐";
        }
        if (recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.AuxiliaryConsensus
            || recognition.Result.Source == MapRecognitionSource.AuxiliaryAnchorTracking
            || diagnostics is
            {
                TrackingMode: MapAlignmentTrackingMode.AuxiliaryAnchorTracking,
                StructureAttempted: false
            })
        {
            return "楼层/辅助特征对齐";
        }
        if (recognition.Result.EvidenceKind == MapAlignmentEvidenceKind.Structure
            || recognition.Result.Source == MapRecognitionSource.StructureMatching)
        {
            return "无门结构对齐";
        }
        return "地图对齐";
    }
}
