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
        _scanProgressOverlay.Report(0.06d, "正在准备扫描...");
        // 开图动画等待（在调用线程即可，不阻塞）
        await Task.Delay(
            _settings!.SessionTuning.OpeningAnimationDelayMilliseconds,
            cancellationToken);

        // 首次识别和重新开图对齐使用同一稳定帧约束，避免把开图动画
        // 或尚未稳定的裁剪/缩放送入侧门身份扫描。
        var frame = await CaptureStableViewportAsync(
            "首次扫描",
            cancellationToken);
        if (frame is null)
        {
            ReportScanCaptureFailure(
                _lastStableCaptureFailureReason ?? "地图截图失败。",
                scanWallClock);
            return;
        }

        _scanProgressOverlay.Report(0.22d, "正在分析画面...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ApplySelectedResolutionPresetAsync(frame.ClientBounds);
            var initialState = new InitialRecognitionPipelineState();
            _scanProgressOverlay.Report(0.38d, "正在识别地图...");
            await Task.Run(() => RunInitialRecognition(frame, initialState));

            _scanProgressOverlay.Report(0.88d, "正在确认结果...");

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
            if (pendingSideEntranceScan is not null
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
                        MapScaleSeedResolver.CreateStrictInitialIdentityValidationTuning(
                            CreateInitialAlignmentStructureTuning());
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
                    var selectedVerified =
                        SideEntranceCandidateEvidence.ApplyStructureAttempt(
                            selectedCandidate,
                            selectedAttempt);
                    if (selectedAttempt.Recognition is { } selectedRecognition
                        && selectedVerified)
                    {
                        recognition = selectedRecognition;
                        _pendingAlignmentIdentity = selectedRecognition;
                        _pendingAlignmentSeed = selectedSeed;
                        _statusMessage =
                            $"侧门地图已确认并完成对齐：{recognition.Map.DisplayName} · "
                            + $"置信度 {recognition.Result.Confidence:P0}";
                    }
                    else
                    {
                        // 玩家已确认候选：身份立即锁定，与变换解耦。结构对齐失败
                        // 只丢变换、不清空身份。身份与侧门种子保留在 pending，供下次
                        // 开图经 recoveringSelectedIdentity 链路对该地图重试对齐
                        // （Pipeline.cs:22-23）。"身份-only" 范式复用
                        // Default.cs:177-192：不设 OverlayTransform、LocalizationConfidence=0。
                        var floorKey = selectedCandidate.FloorKey;
                        if (MapFloorRules.GetFloorProfile(
                                selectedCandidate.Map, floorKey) is null)
                        {
                            floorKey = MapFloorRules.GetPrimaryFloorKey(
                                selectedCandidate.Map);
                        }

                        _pendingAlignmentIdentity = new RuntimeMapRecognition
                        {
                            Map = selectedCandidate.Map,
                            FloorImagePath = _mapRepository.GetFloorOverlayPath(
                                selectedCandidate.Map, floorKey),
                            Result = new MapRecognitionResult
                            {
                                MapId = selectedCandidate.Map.Id,
                                Floor = floorKey,
                                Confidence = selectedCandidate.MatchScore,
                                IdentityConfidence = 1.0d,   // 用户确认 = 身份确定
                                LocalizationConfidence = 0d, // 变换未就绪
                                Source = MapRecognitionSource.UserConfirmed
                            }
                        };
                        // 必须与身份成对设置，否则重试时 Pipeline.cs:119 的
                        // MapAlignmentSession.FromRecognition 会对 null 变换抛异常。
                        _pendingAlignmentSeed = selectedSeed;
                        recognition = null; // 跳过 overlay 提交块

                        var selectedFailure = string.IsNullOrWhiteSpace(
                            selectedAttempt.StructureFailureReason)
                            ? selectedAttempt.FailureReason
                            : selectedAttempt.StructureFailureReason;
                        failureReason =
                            $"已锁定所选地图身份（{selectedCandidate.Map.DisplayName}），但首次对齐未通过："
                            + $"{selectedFailure}；请重新打开地图重试对齐。";
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
                _scanProgressOverlay.Report(0.92d, "正在应用结果...");
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                _hasCompletedQuickScanAlignment = true;
                repairCacheKeys.TryGetValue(
                    recognition.Map.Id,
                    out var repairCacheKey);
                var playerDecidedScale = false;
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
                    playerDecidedScale = true;
                    _logCollector.Append(
                        MapLogCategory.Session,
                        MapLogLevel.Info,
                        $"玩家已决定缩放值 · map={recognition.Map.Id} · "
                        + $"floor={recognition.Result.Floor} · "
                        + $"scale={playerTransform.ScaleX:F6} · "
                        + $"offset=({playerTransform.OffsetX:F0},"
                        + $"{playerTransform.OffsetY:F0})");
                }
                var adaptiveDecision = await EvaluateAdaptiveInitialAsync(
                    recognition,
                    frame,
                    _lastDiagnostics,
                    playerDecidedScale ? MapFeatureCacheSource.Player : null);
                recognition = adaptiveDecision.RecognitionToRender;
                if (adaptiveDecision.AllowLegacyCacheWrite)
                {
                    await RepairMapCacheAsync(repairCacheKey, recognition, frame);
                    await PersistPreprocessedScaleAsync(
                        recognition,
                        frame,
                        _lastDiagnostics);
                    if (playerDecidedScale)
                        await PersistPlayerDecidedScaleAsync(recognition, frame);
                    RecordSuccessfulAlignment(recognition, frame);
                }
                if (!IsCurrentMatchOperation(operationMatch))
                    return;
                _lastRecognition = recognition;
                _pendingAlignmentIdentity = null;
                _pendingAlignmentSeed = null;
                // 侧门扫描时 _lastAlignmentSession 可能尚未初始化；
                // 此时回退到 Task.Run 内部捕获的 pendingSideEntranceSeed，
                // 以保留 SideEntranceScanPriorConfidence。
                _lastAlignmentSession = UpdateAlignmentSession(
                    _lastAlignmentSession ?? pendingSideEntranceSeed,
                    recognition);
                if (adaptiveDecision.AllowReliableSession)
                {
                    RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
                    RememberReliableFloorAlignment(
                        operationMatch,
                        recognition,
                        _lastAlignmentSession);
                }
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
                    await StartOrbTrackingAsync(recognition, frame);
                _logCollector.Append(
                    MapLogCategory.Overlay,
                    MapLogLevel.Info,
                    $"Overlay 更新完成 · visible={_overlay.IsVisible} · hasMap={_overlay.HasMap}");
                // 识别成功后刷新持久小地图（若启用）
                RefreshMiniMapForCurrentFloor();
                _scanProgressOverlay.Report(0.98d, "正在完成...");
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
