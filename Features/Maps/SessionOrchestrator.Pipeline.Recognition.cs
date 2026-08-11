// IDVB Remaster — Session Orchestrator 识别管线

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task RunRecognitionPipelineCoreAsync(
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        var scanWallClock = Stopwatch.StartNew();
        // 开图动画等待（在调用线程即可，不阻塞）
        await Task.Delay(
            _settings!.SessionTuning.OpeningAnimationDelayMilliseconds,
            cancellationToken);

        // 捕获稳定帧
        if (!_captureSvc.TryCaptureViewport(
                ResolveMapViewportForCurrentWindow(),
                out var frameObj,
                out var captureFailureReason)
            || frameObj is not CapturedGameFrame frame)
        {
            ReportScanCaptureFailure(captureFailureReason, scanWallClock);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await TryAutoMatchResolutionPresetAsync(frame.ClientBounds);
            var initialState = new InitialRecognitionPipelineState();
            await Task.Run(() => RunInitialRecognition(frame, initialState));

            var recognition = initialState.Recognition;
            var failureReason = initialState.FailureReason;
            var pendingChoices = initialState.PendingChoices;
            var pendingChoicesReason = initialState.PendingChoicesReason;
            var pendingSideEntranceSeed = initialState.PendingSideEntranceSeed;
            var pendingSideEntranceIdentity = initialState.PendingSideEntranceIdentity;
            var pendingSideEntranceScan = initialState.PendingSideEntranceScan;
            var repairCacheKeys = initialState.RepairCacheKeys;
            var scanSucceeded = initialState.ScanSucceeded;

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
                return;

            if (recognition is null
                && pendingSideEntranceIdentity is not null)
            {
                _pendingAlignmentIdentity = pendingSideEntranceIdentity;
                _pendingAlignmentSeed = pendingSideEntranceSeed;
            }

            if (scanSucceeded)
            {
                _gameMapToggleState.MarkOpen();
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    "扫描成功，已同步原生地图为打开状态；下一次地图键将执行关闭，随后重新打开时执行对齐。");
            }

            // ── 候选地图选择：当识别有歧义或 ForceCandidateSelection 开启时 ──
            if (recognition is null
                && (pendingChoices is { Count: > 0 }
                    || !_headless
                    || _activeCandidateSelector is not null))
            {
                var candidateResolution = await ResolveCandidateSelectionAsync(
                    frame,
                    pendingChoices ?? [],
                    string.IsNullOrWhiteSpace(pendingChoicesReason)
                        ? failureReason ?? "未找到可确认的已记录地图。"
                        : pendingChoicesReason,
                    cancellationToken);
                if (candidateResolution.StartSurvey)
                {
                    await ActivateSurveyFromQuickScanAsync(
                        frame,
                        operationMatch,
                        cancellationToken);
                    return;
                }
                recognition = candidateResolution.Recognition;
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
            }

            // A side-entrance candidate is only scan evidence. Once the user
            // (or headless policy) confirms it, run exactly one selected-map
            // alignment using the mandatory scanned gate as the provisional
            // seed. Never render the provisional scan transform.
            if (pendingSideEntranceScan is { Gate: { } selectedSideGate }
                && recognition is not null
                && pendingSideEntranceSeed is null)
            {
                var selectedCandidate = pendingSideEntranceScan.Candidates
                    .FirstOrDefault(candidate => candidate.Map.Id == recognition.Map.Id);
                if (selectedCandidate is null)
                {
                    recognition = null;
                    failureReason =
                        "侧门候选已选择，但候选不属于本次扫描结果，未提交地图锁定。";
                }
                else if (!_recognition.TryCreateSideEntranceAlignmentSeed(
                             selectedCandidate,
                             selectedSideGate,
                             frame.ViewportBounds,
                             out var selectedSeed,
                             out var selectedSeedFailure))
                {
                    recognition = null;
                    failureReason = $"侧门扫描种子无效：{selectedSeedFailure}";
                }
                else
                {
                    pendingSideEntranceSeed = selectedSeed;
                    _pendingAlignmentIdentity = recognition;
                    _pendingAlignmentSeed = selectedSeed;
                    var selectedTuning = CreateInitialAlignmentRecognitionTuning();
                    if (selectedTuning.GateTemplateThreshold
                        > GateTemplateRules.FallbackPairThreshold)
                    {
                        selectedTuning.GateTemplateThreshold =
                            GateTemplateRules.FallbackPairThreshold;
                    }

                    var selectedSearchContext =
                        CreateSideEntranceSearchContext(
                            selectedSeed,
                            selectedTuning,
                            useInitialHighPrecisionRecovery: true);
                    var selectedStructureTuning =
                        CreateInitialAlignmentStructureTuning();
                    MapFeatureCacheKey? selectedRepairKey = null;
                    var selectedAttempt = await Task.Run(() =>
                    {
                        MapRecognitionAttempt AlignSelectedSide() =>
                            _recognition.AlignSideEntrance(
                                frame,
                                selectedSeed.MapId,
                                selectedSeed,
                                _settings.OverlayAlignmentMode,
                                selectedTuning,
                                selectedStructureTuning,
                                alignmentSearchContext: selectedSearchContext);
                        return AlignUsingScaleCache(
                            frame,
                            selectedCandidate.Map,
                            selectedSeed.FloorKey,
                            selectedTuning,
                            selectedStructureTuning,
                            selectedSeed.SideEntranceScanPriorConfidence,
                            AlignSelectedSide,
                            out selectedRepairKey);
                    });
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentMatchOperation(operationMatch))
                        return;
                    if (selectedRepairKey is not null)
                        repairCacheKeys[selectedSeed.MapId] = selectedRepairKey;
                    _lastDiagnostics = selectedAttempt.Diagnostics;
                    RecordResearchAttempt(
                        selectedCandidate.Map,
                        selectedSeed.FloorKey,
                        frame,
                        selectedAttempt,
                        "side-entrance-confirmation");
                    if (selectedAttempt.Recognition is { } selectedRecognition)
                    {
                        recognition = selectedRecognition;
                        _statusMessage =
                            $"侧门地图已确认并完成对齐：{recognition.Map.DisplayName} · "
                            + $"置信度 {recognition.Result.Confidence:P0}";
                    }
                    else
                    {
                        recognition = null;
                        var selectedFailure = string.IsNullOrWhiteSpace(
                            selectedAttempt.StructureFailureReason)
                            ? selectedAttempt.FailureReason
                            : selectedAttempt.StructureFailureReason;
                        failureReason =
                            $"侧门地图已选择，但首次对齐失败：{selectedFailure}；"
                            + "已保留所选地图身份，下次重新打开地图将按高精度策略重试。";
                    }
                }
            }

            if (recognition is { } identityLock)
            {
                var manualFloorAlignment =
                    await AlignQuickScanManualFloorAsync(
                        frame,
                        identityLock,
                        cancellationToken);
                recognition = manualFloorAlignment.Recognition;
                failureReason ??= manualFloorAlignment.FailureReason;
                _lastDiagnostics =
                    manualFloorAlignment.Diagnostics ?? _lastDiagnostics;
                if (manualFloorAlignment.RepairCacheKey is { } manualRepairKey)
                    repairCacheKeys[identityLock.Map.Id] = manualRepairKey;
                if (manualFloorAlignment.Attempt is { } manualFloorAttempt)
                {
                    RecordResearchAttempt(
                        identityLock.Map,
                        manualFloorAttempt.Recognition?.Result.Floor
                            ?? _currentFloorKey
                            ?? MapFloorRules.GetPrimaryFloorKey(identityLock.Map),
                        frame,
                        manualFloorAttempt,
                        "floor-switch");
                }
            }

            if (recognition is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                _hasCompletedQuickScanAlignment = true;
                repairCacheKeys.TryGetValue(
                    recognition.Map.Id,
                    out var repairCacheKey);
                await RepairMapCacheAsync(repairCacheKey, recognition, frame);
                await PersistPreprocessedScaleAsync(
                    recognition,
                    frame,
                    _lastDiagnostics);
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                RecordSuccessfulAlignment(recognition, frame);
                // ── 由玩家决定缩放值：确认后直接以玩家 transform 渲染 ──
                if (_settings.RecognitionTuning.PlayerDecidesScale
                    && recognition.Result.OverlayTransform is { } initialTransform
                    && await MapManualTransformWindow.ShowAsync(
                        frame,
                        recognition,
                        initialTransform,
                        cancellationToken) is { } playerTransform)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentMatchOperation(operationMatch))
                        return;
                    recognition = WithOverlayTransform(
                        recognition,
                        playerTransform);
                    await PersistPlayerDecidedScaleAsync(recognition, frame);
                    _logCollector.Append(
                        MapLogCategory.Session,
                        MapLogLevel.Info,
                        $"玩家已决定缩放值 · map={recognition.Map.Id} · "
                        + $"floor={recognition.Result.Floor} · "
                        + $"scale={playerTransform.ScaleX:F6} · "
                        + $"offset=({playerTransform.OffsetX:F0},"
                        + $"{playerTransform.OffsetY:F0})");
                }
                _lastRecognition = recognition;
                _pendingAlignmentIdentity = null;
                _pendingAlignmentSeed = null;
                // 侧门扫描时 _lastAlignmentSession 可能尚未初始化；
                // 此时回退到 Task.Run 内部捕获的 pendingSideEntranceSeed，
                // 以保留 SideEntranceScanPriorConfidence。
                _lastAlignmentSession = UpdateAlignmentSession(
                    _lastAlignmentSession ?? pendingSideEntranceSeed,
                    recognition);
                RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
                RememberReliableFloorAlignment(
                    operationMatch,
                    recognition,
                    _lastAlignmentSession);
                _lastGameBounds = frame.ClientBounds;
                _lastGameWindowHandle = frame.WindowHandle;

                _logCollector.Append(
                    MapLogCategory.Overlay,
                    MapLogLevel.Info,
                    $"开始更新 Overlay · map={recognition.Map.Id} · floor={recognition.Result.Floor} · "
                    + $"imageExists={File.Exists(recognition.FloorImagePath)} · visibleBefore={_overlay.IsVisible}",
                    details: new()
                    {
                        ["floorImagePath"] = recognition.FloorImagePath,
                        ["windowHandle"] = $"0x{frame.WindowHandle.ToInt64():X}",
                        ["gameBounds"] = $"{frame.ClientBounds.X:F0},{frame.ClientBounds.Y:F0},{frame.ClientBounds.Width:F0}x{frame.ClientBounds.Height:F0}",
                        ["hasTransform"] = recognition.Result.OverlayTransform is not null
                    });
                _overlay.UpdateMap(
                    recognition,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _settings.ShowOverlayStatus);
                ShowTransientAlignmentSuccess(
                    recognition,
                    frame.ClientBounds,
                    frame.WindowHandle,
                    _lastDiagnostics);
                _overlay.Show();
                _logCollector.Append(
                    MapLogCategory.Overlay,
                    MapLogLevel.Info,
                    $"Overlay 更新完成 · visible={_overlay.IsVisible} · hasMap={_overlay.HasMap}");
                // 识别成功后刷新持久小地图（若启用）
                RefreshMiniMapForCurrentFloor();
            }
            else if (failureReason is not null)
            {
                _statusMessage = failureReason;
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    _statusMessage);
                ShowTransientOverlayStatus(
                    MapOverlayStatusLevel.Failure,
                    "Overlay 识别失败",
                    failureReason,
                    "请查看日志中的扫描、对齐和 Overlay 记录。",
                    frame.ClientBounds,
                    frame.WindowHandle);
                RefreshMiniMapForCurrentFloor();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _statusMessage = $"识别异常：{ex.Message}";
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Error,
                _statusMessage,
                details: new()
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["stackTrace"] = ex.ToString()
                });
            ShowTransientOverlayStatus(
                MapOverlayStatusLevel.Failure,
                "Overlay 识别异常",
                _statusMessage,
                "请查看日志中的异常堆栈。",
                frame.ClientBounds,
                frame.WindowHandle);
        }
        finally
        {
            frame.Dispose();
        }
    }
}
