namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly SemaphoreSlim _matchLifecycleGate = new(1, 1);
    private CancellationTokenSource? _matchCancellation;
    private readonly object _mapOpenCancellationGate = new();
    private CancellationTokenSource? _mapOpenCancellation;
    private int _matchEnding;

    // A user-confirmed map remains useful evidence even when its first
    // alignment attempt fails. Keep that identity and its scan seed separate
    // from _lastRecognition so an unverified transform is never rendered.
    private RuntimeMapRecognition? _pendingAlignmentIdentity;
    private MapAlignmentSession? _pendingAlignmentSeed;

    private bool IsMatchEnding => Volatile.Read(ref _matchEnding) != 0;

    private CancellationToken CurrentMatchCancellationToken =>
        _matchCancellation?.Token ?? new CancellationToken(canceled: true);

    private bool IsCurrentMatchOperation(MapMatchSnapshot operationMatch) =>
        !IsMatchEnding && _matchSession.IsCurrent(operationMatch);

    private void StartMatchCancellationScope()
    {
        _matchCancellation?.Cancel();
        _matchCancellation?.Dispose();
        _matchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCts.Token);
    }

    private void CancelMatchOperations()
    {
        EndAdaptiveMapOpen("match lifecycle changed");
        CancelOrbTracking("match lifecycle changed");
        CancelMapOpenAlignment();
        _lowStructureRecoveryCursor.Reset();
        try
        {
            _matchCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposal and match shutdown can race during application exit.
        }
        _alignmentCommitGuard.Invalidate();
        _gameMapToggleState.Reset();
    }

    private CancellationTokenSource BeginMapOpenCancellationScope()
    {
        lock (_mapOpenCancellationGate)
        {
            _mapOpenCancellation?.Cancel();
            var scope = CancellationTokenSource.CreateLinkedTokenSource(
                CurrentMatchCancellationToken);
            _mapOpenCancellation = scope;
            return scope;
        }
    }

    private void CompleteMapOpenCancellationScope(
        CancellationTokenSource scope)
    {
        lock (_mapOpenCancellationGate)
        {
            if (ReferenceEquals(_mapOpenCancellation, scope))
                _mapOpenCancellation = null;
        }
        scope.Dispose();
    }

    private void CancelMapOpenAlignment()
    {
        lock (_mapOpenCancellationGate)
            _mapOpenCancellation?.Cancel();
        _lowStructureRecoveryCursor.Reset();
    }

    private async Task DrainMatchOperationsAsync()
    {
        await DrainOrbTrackingAsync();
        await _scanGate.WaitAsync();
        _scanGate.Release();
    }

    /// <summary>
    /// A quick scan is an explicit request to identify the map again. Release
    /// every map-scoped lock before the new scan starts so a previous wrong
    /// choice cannot constrain the result or remain visible when rescanning
    /// fails or is cancelled.
    /// </summary>
    private void UnlockMapForRescan()
    {
        // 再次快捷扫描是显式请求重新识别：作废尚未消费的后台扫描结果。
        ClearPendingBackgroundScan();
        _lowStructureRecoveryCursor.Reset();
        EndAdaptiveMapOpen("map rescan requested");
        var previousMapId = _lastRecognition?.Map.Id
            ?? _pendingAlignmentIdentity?.Map.Id;
        if (previousMapId is null
            && _lastAlignmentSession is null
            && _primaryFloorAlignmentSession is null)
        {
            return;
        }

        _overlayStatus.Clear();
        _overlay.Clear();
        _mapOpenSession.Close("quick scan restarted");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _recognition.ResetMatchState();

        _currentFloorKey = null;
        _mapLease.Clear();
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        ClearAdaptiveSessionKeys();
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;

        lock (_reliableFloorAlignmentGate)
        {
            _reliableFloorAlignments.Clear();
        }
        ClearManualFloorScaleLocks();
        ClearMapViewportPresenceReferences();

        // Do not allow samples collected for a wrongly selected map to be
        // persisted after a later scan corrects the identity.
        ResetAutomaticMapCacheSamples();

        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            $"重新扫描已解除地图锁定 · previousMap={previousMapId?.ToString() ?? "<none>"}");
    }

    private void ResetMatchTransientState(bool resetAutomaticCacheSamples)
    {
        // 对局结束：作废尚未消费的后台扫描结果，下一局重新开始。
        ClearPendingBackgroundScan();
        _lowStructureRecoveryCursor.Reset();
        EndAdaptiveMapOpen("match transient state reset");
        _overlayStatus.Clear();
        _overlay.Clear();
        _mapOpenSession.Close("match lifecycle reset");
        _candidateStability.Reset();
        _alignmentCommitGuard.Invalidate();
        _gameMapToggleState.Reset();
        _recognition.ResetMatchState();

        _activeCandidateSelector = null;
        _lastCandidateChoices = [];
        _manualSelectionActive = false;
        _currentFloorKey = null;
        _mapLease.Clear();
        _lastRecognition = null;
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastAlignmentSession = null;
        _primaryFloorAlignmentSession = null;
        ClearAdaptiveSessionKeys();
        _lastDiagnostics = null;
        _lastScanPhaseTimings = null;
        _lastAlignmentPhaseTimings = null;
        _lastScanOperationTrace = null;
        _lastAlignmentOperationTrace = null;
        _lastCandidateOperationTrace = null;
        _lastStableCaptureFailureReason = null;
        _lastFloorRecognition = null;
        _lastTrustedPlayerPoint = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lastGameBounds = default;
        _lastGameWindowHandle = IntPtr.Zero;
        lock (_reliableFloorAlignmentGate)
            _reliableFloorAlignments.Clear();
        ClearManualFloorScaleLocks();
        ClearMapViewportPresenceReferences();

        if (resetAutomaticCacheSamples)
            ResetAutomaticMapCacheSamples();
    }
}
/*
 * 文件职责：SessionOrchestrator.MatchLifecycle。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
