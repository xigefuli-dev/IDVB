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

    public void KeepCurrent()
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
/*
 * 文件职责：MapOverlayStatusCoordinator。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
