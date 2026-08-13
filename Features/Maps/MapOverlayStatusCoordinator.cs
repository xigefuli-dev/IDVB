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
        if (_disposed)
            return;
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

        TryOverlayOperation(
            "status-show",
            () => _overlay.UpdateStatus(
                status,
                gameBounds,
                gameWindowHandle,
                showStatusPreference,
                showImmediately: true));
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
        TryOverlayOperation("status-clear", _overlay.ClearStatus);
    }

    private async Task ExpireAsync(long version, CancellationTokenSource expiration)
    {
        try
        {
            await Task.Delay(_transientLifetime, _timeProvider, expiration.Token);
            try
            {
                _dispatch(() => ClearIfCurrent(version, expiration));
            }
            catch (Exception exception)
            {
                LogOverlayFailure("status-expiration-dispatch", exception);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            LogOverlayFailure("status-expiration", exception);
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
        TryOverlayOperation("status-expiration-clear", _overlay.ClearStatus);
    }

    private static void LogOverlayFailure(
        string operation,
        Exception exception)
    {
        try
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                $"Overlay operation failed: {operation}",
                details: new()
                {
                    ["outcome"] = "overlay-operation-failed",
                    ["operation"] = operation,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
        }
        catch
        {
        }
    }

    private static void TryOverlayOperation(
        string operation,
        Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            LogOverlayFailure(operation, exception);
        }
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

internal static class SurveyCaptureCleanup
{
    internal static void Complete(
        SemaphoreSlim scanGate,
        Action restoreOverlay,
        Action notifyStateChanged,
        Action<string, Exception> reportFailure)
    {
        try
        {
            scanGate.Release();
        }
        catch (Exception exception)
        {
            TryReport(reportFailure, "scan-gate-release", exception);
        }

        TryRun(restoreOverlay, reportFailure, "overlay-restore");
        TryRun(notifyStateChanged, reportFailure, "state-changed");
    }

    private static void TryRun(
        Action action,
        Action<string, Exception> reportFailure,
        string operation)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            TryReport(reportFailure, operation, exception);
        }
    }

    private static void TryReport(
        Action<string, Exception> reportFailure,
        string operation,
        Exception exception)
    {
        try
        {
            reportFailure(operation, exception);
        }
        catch
        {
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
