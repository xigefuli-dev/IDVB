// IDVB Remaster — Session Orchestrator（新架构唯一入口）
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator : ISessionOrchestrator, IDisposable, IAsyncDisposable
{
    public async Task RunManualRecognitionAsync()
    {
        if (_disposed || !_settings!.IsEnabled)
            return;
        var operationMatch = _matchSession.Snapshot;
        if (!operationMatch.IsStarted || IsMatchEnding)
            return;
        var cancellationToken = CurrentMatchCancellationToken;
        if (!_captureSvc.TryGetForegroundClientBounds(out _, out _, out _))
            return;

        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有扫描正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var trace = BeginMapOperationTrace(
            MapOperationTypes.CandidateConfirmation,
            CandidateConfirmationTracePhases,
            route: "manual-recognition");
        var outcome = "success";
        var terminalReason = "completed";
        var traceFinished = false;
        try
        {
            trace.StartTopLevel("route_prepare").Complete();
            await RunManualRecognitionCoreAsync(
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"手动识别已取消 · matchVersion={operationMatch.Version}");
            outcome = "cancelled";
            terminalReason = "match-cancellation";
        }
        catch (Exception ex)
        {
            outcome = "failed";
            terminalReason = $"exception:{ex.GetType().Name}";
            throw;
        }
        finally
        {
            try
            {
                using (trace.StartTopLevel("cleanup"))
                    _scanGate.Release();
            }
            finally
            {
                if (!traceFinished)
                {
                    FinishMapOperationTrace(
                        trace,
                        isAlignment: false,
                        outcome,
                        terminalReason);
                    traceFinished = true;
                }
            }
        }
    }

    /// <summary>
    /// 手动识别：冻结游戏画面 → 弹窗框选大门/侧门 → 手动几何排名 →
    /// 若有歧义弹候选窗口供玩家选择 → 应用结果到 Overlay。
    /// 该链路恢复自旧 MapRuntimeService.ManualRecognition.cs 的完整交互。
    /// </summary>
    private async Task RunManualRecognitionCoreAsync(
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        // 冻结画面：捕获整个客户区，让玩家在拖框窗口内框选双门
        var manualCapture = MapOperationTraceAmbient.StartTopLevel(
            "manual_capture",
            MapOperationWaitKind.Capture);
        if (!_captureSvc.TryCaptureClient(out var frameObj, out _)
            || frameObj is not CapturedGameFrame capturedFrame)
        {
            manualCapture.Complete();
            ActiveOperationTrace?.SetTerminal("failed", "manual-capture-failed");
            _statusMessage = "手动识别截图失败，请保持游戏在前台并打开地图。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var frame = capturedFrame;
        MapOperationTrace.MapOperationSpanScope? selectedAlignment = null;
        try
        {
            var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(
                frame.ClientBounds,
                ResolveViewportRegion(
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height)));
            if (!viewportBounds.IsValid)
            {
                manualCapture.Complete();
                ActiveOperationTrace?.SetTerminal("failed", "invalid-viewport-bounds");
                _statusMessage = "已校准的地图区域无效，请重新校准。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            manualCapture.Complete();

            _statusMessage = "手动识别中……请框选大门和侧门。";
            StateChanged?.Invoke(this, EventArgs.Empty);

            ManualGateSelectionResult? selection;
            _manualSelectionActive = true;
            try
            {
                using var selectionWait = MapOperationTraceAmbient.StartTopLevel(
                    "candidate_selection_wait",
                    MapOperationWaitKind.User);
                selection = await MapManualRecognitionWindow.ShowAsync(
                        frame,
                        viewportBounds,
                        cancellationToken,
                        _captureProtection);
            }
            finally
            {
                _manualSelectionActive = false;
            }

            if (selection is null)
            {
                ActiveOperationTrace?.SetTerminal(
                    "cancelled",
                    "manual-selection-cancelled");
                _statusMessage = "已取消手动识别。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            MapRecognitionAttempt attempt;
            var manualDispatch = MapOperationTraceAmbient.StartChild(
                "manual_recognition_dispatch_wait",
                MapOperationWaitKind.Queue);
            try
            {
                using var manualRecognition = MapOperationTraceAmbient.StartTopLevel(
                    "manual_recognition",
                    MapOperationWaitKind.Compute);
                attempt = await Task.Run(
                    () =>
                    {
                        manualDispatch.Complete();
                        using var manualWorker = MapOperationTraceAmbient.StartChild(
                            "manual_recognition_worker_execution",
                            MapOperationWaitKind.Compute);
                        return _recognition.RecognizeManual(
                            viewportBounds,
                            selection.MainGateBounds,
                            selection.SideGateBounds,
                            _settings!.OverlayAlignmentMode,
                            _settings.RecognitionTuning.Clone(),
                            mapClass: operationMatch.MapClass);
                    });
            }
            finally
            {
                manualDispatch.Complete();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
            {
                ActiveOperationTrace?.SetTerminal(
                    "superseded",
                    "match-operation-version-changed");
                return;
            }

            _lastDiagnostics = attempt.Diagnostics;

            RuntimeMapRecognition? recognition = attempt.Recognition;
            if (recognition is null && attempt.Choices.Count > 0)
            {
                var orderedChoices = attempt.Choices
                    .OrderBy(candidate => candidate.IsReferenceOnly)
                    .ThenBy(candidate => candidate.PreferredOrder)
                    .ThenByDescending(candidate => candidate.RawConfidence)
                    .ToArray();
                var displayChoices = await BuildNativeCandidateChoicesAsync(
                    orderedChoices,
                    operationMatch.MapClass!);
                MapCandidateDecision decision;
                using (MapOperationTraceAmbient.StartTopLevel(
                           "candidate_selection_wait",
                           MapOperationWaitKind.User))
                {
                    decision = await MapManualCandidateWindow.ShowAsync(
                        frame,
                        displayChoices,
                        attempt.FailureReason,
                        cancellationToken,
                        _captureProtection,
                        _mapRepository,
                        viewportBounds);
                }
                if (decision.Kind == MapCandidateDecisionKind.StartSurvey)
                {
                    // 转入测绘：显式取代识别结果，作废尚未消费的后台扫描。
                    ActiveOperationTrace?.SetTerminal("superseded", "survey-started");
                    ClearPendingBackgroundScan();
                    await ActivateSurveyFromQuickScanAsync(
                        frame,
                        operationMatch,
                        cancellationToken);
                    return;
                }
                if (decision.Kind != MapCandidateDecisionKind.SelectKnownMap
                    || decision.CandidateIndex is not { } selectedIndex
                    || selectedIndex < 0
                    || selectedIndex >= displayChoices.Count)
                {
                    ActiveOperationTrace?.SetTerminal(
                        "cancelled",
                        "candidate-selection-cancelled");
                    _statusMessage = "已取消候选确认。";
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                var selectedChoice = displayChoices[selectedIndex];
                var cameFromRecognitionCandidates = orderedChoices.Any(
                    candidate => candidate.Recognition.Map.Id
                        == selectedChoice.Recognition.Map.Id);
                if (cameFromRecognitionCandidates)
                {
                    recognition = MapCvRecognitionService.ConfirmChoice(
                        selectedChoice);
                }
                else
                {
                    var selectedManualRecognition =
                        _recognition.RecognizeManualSelectedMap(
                            selectedChoice.Recognition.Map.Id,
                            viewportBounds,
                            selection.MainGateBounds,
                            selection.SideGateBounds,
                            _settings!.OverlayAlignmentMode,
                            _settings.RecognitionTuning.Clone(),
                            out var selectedFailure);
                    if (selectedManualRecognition is null)
                    {
                        ActiveOperationTrace?.SetTerminal(
                            "failed",
                            "catalog-map-manual-alignment-failed");
                        _statusMessage =
                            $"无法对齐所选地图：{selectedFailure}";
                        StateChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    recognition = MapCvRecognitionService.ConfirmChoice(
                        new MapRecognitionChoice
                        {
                            Recognition = selectedManualRecognition
                        });
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
            {
                ActiveOperationTrace?.SetTerminal(
                    "superseded",
                    "match-operation-version-changed");
                return;
            }

            if (recognition is null)
            {
                ActiveOperationTrace?.SetTerminal(
                    "failed",
                    "manual-recognition-failed");
                _statusMessage = $"手动识别失败：{attempt.FailureReason}";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            selectedAlignment = MapOperationTraceAmbient.StartTopLevel(
                "selected_candidate_alignment",
                MapOperationWaitKind.Compute,
                mapId: recognition.Map.Id.ToString("D"),
                floorKey: recognition.Result.Floor);

            // A manual result is an explicit replacement of the current
            // alignment. Invalidate the old tracker and runtime scale before
            // the new result enters adaptive arbitration, even when map/floor
            // identity did not change.
            CancelOrbTracking("manual recognition result replacing alignment");
            await DrainOrbTrackingAsync();
            EndAdaptiveMapOpen("manual recognition result replacing alignment");
            ClearAdaptiveSessionKeys();
            _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);

            RecordResearchAttempt(
                recognition.Map,
                recognition.Result.Floor,
                frame,
                attempt,
                "manual-recognition",
                recognitionOverride: recognition);
            var adaptiveDecision = await EvaluateAdaptiveInitialAsync(
                recognition,
                frame,
                attempt.Diagnostics);
            recognition = adaptiveDecision.RecognitionToRender;
            // 与仅对齐/首次识别一致：参考签名写入不受 AllowLegacyCacheWrite 门控，
            // 否则 provisional 时不记录，下次开图「仅对齐」就绪判定缺 reference 走
            // blue-gray 兜底分支（首帧必拒）。
            RememberMapViewportPresenceReference(recognition, frame);
            selectedAlignment.Complete();
            if (adaptiveDecision.AllowLegacyCacheWrite)
            {
                using var persistence = MapOperationTraceAmbient.StartTopLevel(
                    "persistence",
                    MapOperationWaitKind.Io,
                    mapId: recognition.Map.Id.ToString("D"),
                    floorKey: recognition.Result.Floor);
                RecordSuccessfulAlignment(recognition, frame);
                await PersistPreprocessedScaleAsync(
                        recognition,
                        frame,
                        attempt.Diagnostics);
            }
            using (MapOperationTraceAmbient.StartTopLevel(
                       "session_commit",
                       MapOperationWaitKind.Compute,
                       mapId: recognition.Map.Id.ToString("D"),
                       floorKey: recognition.Result.Floor))
            {
                _lastRecognition = recognition;
                _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);
            }
            // 手动识别是显式锁定地图：作废尚未消费的后台扫描结果，
            // 否则下次开图 HandleGameMapToggleAsync 会按 IsBackgroundScanCompleted
            // 走后台消费，用旧身份重新对齐并覆盖本次手动结果。
            ClearPendingBackgroundScan();
            _lastAlignmentSession = UpdateAlignmentSession(
                _lastAlignmentSession,
                recognition);
            if (adaptiveDecision.AllowReliableSession)
            {
                RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
            }
            RememberReliableFloorAlignment(
                operationMatch,
                recognition,
                _lastAlignmentSession,
                frame,
                adaptiveDecision.AllowReliableSession);
            _lastGameBounds = frame.ClientBounds;
            _lastGameWindowHandle = frame.WindowHandle;
            _statusMessage =
                $"手动识别：{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"手动识别完成 · map={recognition.Map.Id} · floor={recognition.Result.Floor}",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor,
                    ["confidence"] = recognition.Result.Confidence
                });
            using (MapOperationTraceAmbient.StartTopLevel(
                       "overlay_publish",
                       MapOperationWaitKind.Compute,
                       mapId: recognition.Map.Id.ToString("D"),
                       floorKey: recognition.Result.Floor))
            {
                _overlay.UpdateMap(
                    recognition,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _settings!.ShowOverlayStatus);
                if (adaptiveDecision.AllowReliableSession)
                {
                    ShowAdaptiveReliableStatus(
                        recognition,
                        adaptiveDecision,
                        frame.ClientBounds,
                        frame.WindowHandle);
                }
                else
                {
                    ShowAdaptiveProvisionalStatus(
                        recognition,
                        adaptiveDecision,
                        frame.ClientBounds,
                        frame.WindowHandle);
                }
                _overlay.Show();
                if (adaptiveDecision.StartOrbTracking)
                {
                    using var orb = MapOperationTraceAmbient.StartChild(
                        "orb_tracking",
                        MapOperationWaitKind.Compute,
                        mapId: recognition.Map.Id.ToString("D"),
                        floorKey: recognition.Result.Floor);
                    await StartOrbTrackingAsync(recognition, frame);
                }
                using var miniMap = MapOperationTraceAmbient.StartChild(
                    "mini_map_publish",
                    MapOperationWaitKind.Compute,
                    mapId: recognition.Map.Id.ToString("D"),
                    floorKey: recognition.Result.Floor);
                RefreshMiniMapForCurrentFloor();
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            selectedAlignment?.Complete();
            manualCapture.Complete();
            using var frameDispose = MapOperationTraceAmbient.StartChild(
                "frame_dispose",
                MapOperationWaitKind.Io);
            frame.Dispose();
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.ManualRecognition。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
