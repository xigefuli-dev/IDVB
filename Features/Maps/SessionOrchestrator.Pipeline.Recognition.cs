// IDVB Remaster — Session Orchestrator 识别管线

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;
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
        var trace = ActiveOperationTrace;
        // 后台扫描：只静默识别地图（recognizeOnly），完成后早退——
        // 不弹候选/缩放窗口、不对齐、不提交 overlay，全部延迟到开图消费。
        var backgroundMode = _settings!.BackgroundScanEnabled;
        // 开图动画等待（在调用线程即可，不阻塞）
        var openingWait = trace?.StartTopLevel(
            "opening_animation_wait",
            MapOperationWaitKind.Timer);
        try
        {
            _scanProgressOverlay.Report(0.06d, "正在准备扫描...");
            await Task.Delay(
                _settings!.SessionTuning.OpeningAnimationDelayMilliseconds,
                cancellationToken);
        }
        finally
        {
            openingWait?.Complete();
        }

        // 首次识别和重新开图对齐使用同一稳定帧约束，避免把开图动画
        // 或尚未稳定的裁剪/缩放送入侧门身份扫描。
        var stableViewport = trace?.StartTopLevel(
            "stable_viewport",
            MapOperationWaitKind.Capture);
        CapturedGameFrame? frame;
        try
        {
            frame = await CaptureStableViewportAsync(
                "首次扫描",
                cancellationToken);
        }
        finally
        {
            stableViewport?.Complete();
        }
        if (frame is null)
        {
            ReportScanCaptureFailure(
                _lastStableCaptureFailureReason ?? "地图截图失败。",
                scanWallClock);
            trace?.SetTerminal("failed", "stable-viewport-capture-failed");
            return;
        }

        _scanProgressOverlay.Report(0.22d, "正在分析画面...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolutionPreset = trace?.StartTopLevel(
                "resolution_preset",
                MapOperationWaitKind.Io);
            try
            {
                await ApplySelectedResolutionPresetAsync(frame.ClientBounds);
            }
            finally
            {
                resolutionPreset?.Complete();
            }
            var initialState = new InitialRecognitionPipelineState();
            _scanProgressOverlay.Report(0.38d, "正在识别地图...");
            var recognitionDispatch = trace?.StartTopLevel(
                "recognition_dispatch_wait",
                MapOperationWaitKind.Queue);
            try
            {
                await Task.Run(() =>
                {
                    recognitionDispatch?.Complete();
                    using var recognitionWorker = MapOperationTraceAmbient.StartChild(
                        "recognition_worker_execution",
                        MapOperationWaitKind.Compute);
                    RunInitialRecognition(frame, initialState, backgroundMode);
                });
            }
            finally
            {
                recognitionDispatch?.Complete();
            }

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
            {
                trace?.SetTerminal("superseded", "match-operation-version-changed");
                return;
            }

            // 后台扫描：识别完成后立即早退。结果由 CompleteBackgroundScan
            // 保存为完成状态，玩家第一次打开游戏地图时再消费（候选→缩放→对齐）。
            if (backgroundMode)
            {
                CompleteBackgroundScan(initialState);
                return;
            }

            if (recognition is null
                && pendingSideEntranceIdentity is not null)
            {
                _pendingAlignmentIdentity = pendingSideEntranceIdentity;
                _mapLease.Bind(_matchSession.Snapshot, pendingSideEntranceIdentity.Map.Id);
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
                var candidateSelection = trace?.StartTopLevel(
                    "candidate_selection_wait",
                    !_headless || _activeCandidateSelector is not null
                        ? MapOperationWaitKind.User
                        : MapOperationWaitKind.Compute);
                CandidateSelectionResolution candidateResolution;
                try
                {
                    candidateResolution = await ResolveCandidateSelectionAsync(
                        frame,
                        pendingChoices ?? [],
                        string.IsNullOrWhiteSpace(pendingChoicesReason)
                            ? failureReason ?? "未找到可确认的已记录地图。"
                            : pendingChoicesReason,
                        cancellationToken);
                }
                finally
                {
                    candidateSelection?.Complete();
                }
                if (candidateResolution.StartSurvey)
                {
                    trace?.SetTerminal("superseded", "survey-started");
                    await ActivateSurveyFromQuickScanAsync(
                        frame,
                        operationMatch,
                        cancellationToken);
                    return;
                }
                recognition = candidateResolution.Recognition;
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                {
                    trace?.SetTerminal("superseded", "match-operation-version-changed");
                    return;
                }
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
                    // A catalog-tail selection intentionally has no side-door
                    // scan seed. Keep the explicit identity and let the
                    // selected-map manual-floor path below align it from a
                    // neutral seed on this same frame.
                    _logCollector.Append(
                        MapLogCategory.Session,
                        MapLogLevel.Info,
                        $"已选择扫描候选外地图，改用独立对齐 · map={recognition.Map.DisplayName}",
                        details: new()
                        {
                            ["mapId"] = recognition.Map.Id,
                            ["candidateCount"] =
                                pendingSideEntranceScan.Candidates.Count
                        });
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
                    var selectedAlignment = trace?.StartTopLevel(
                        "selected_candidate_alignment",
                        MapOperationWaitKind.Compute,
                        mapId: selectedCandidate.Map.Id.ToString("D"),
                        floorKey: selectedSeed.FloorKey);
                    MapRecognitionAttempt selectedAttempt;
                    var selectedDispatch = MapOperationTraceAmbient.StartChild(
                        "candidate_dispatch_wait",
                        MapOperationWaitKind.Queue,
                        mapId: selectedCandidate.Map.Id.ToString("D"),
                        floorKey: selectedSeed.FloorKey,
                        attemptIndex: 0);
                    try
                    {
                        selectedAttempt = await Task.Run(() =>
                        {
                            selectedDispatch.Complete();
                            using var selectedWorker = MapOperationTraceAmbient.StartChild(
                                "candidate_worker_execution",
                                MapOperationWaitKind.Compute,
                                mapId: selectedCandidate.Map.Id.ToString("D"),
                                floorKey: selectedSeed.FloorKey,
                                attemptIndex: 0);
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
                    }
                    finally
                    {
                        selectedDispatch.Complete();
                        selectedAlignment?.Complete();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentMatchOperation(operationMatch))
                    {
                        trace?.SetTerminal("superseded", "match-operation-version-changed");
                        return;
                    }
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
                        _mapLease.Bind(_matchSession.Snapshot, selectedRecognition.Map.Id);
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
                        _mapLease.Bind(_matchSession.Snapshot, selectedCandidate.Map.Id);
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
                var manualFloor = trace?.StartTopLevel(
                    "manual_floor_alignment",
                    MapOperationWaitKind.Compute,
                    mapId: identityLock.Map.Id.ToString("D"),
                    floorKey: _currentFloorKey);
                (RuntimeMapRecognition? Recognition,
                    string? FailureReason,
                    MapScanDiagnostics? Diagnostics,
                    MapFeatureCacheKey? RepairCacheKey,
                    MapRecognitionAttempt? Attempt) manualFloorAlignment;
                try
                {
                    manualFloorAlignment =
                        await AlignQuickScanManualFloorAsync(
                            frame,
                            identityLock,
                            cancellationToken);
                }
                finally
                {
                    manualFloor?.Complete();
                }
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
                trace?.SetContext(
                    mapId: recognition.Map.Id.ToString("D"),
                    floorKey: recognition.Result.Floor);
                _scanProgressOverlay.Report(0.92d, "正在应用结果...");
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch))
                {
                    trace?.SetTerminal("superseded", "match-operation-version-changed");
                    return;
                }
                _hasCompletedQuickScanAlignment = true;
                repairCacheKeys.TryGetValue(
                    recognition.Map.Id,
                    out var repairCacheKey);
                var playerDecidedScale = false;
                // ── 由玩家决定缩放值：确认后直接以玩家 transform 渲染 ──
                if (_settings.RecognitionTuning.PlayerDecidesScale
                    && recognition.Result.OverlayTransform is { } initialTransform)
                {
                    MapOverlayTransform? playerTransform;
                    using (trace?.StartTopLevel(
                               "candidate_selection_wait",
                               MapOperationWaitKind.User,
                               mapId: recognition.Map.Id.ToString("D"),
                               floorKey: recognition.Result.Floor)
                        ?? MapOperationTrace.MapOperationSpanScope.Noop)
                    {
                        playerTransform = await MapManualTransformWindow.ShowAsync(
                            frame,
                            recognition,
                            initialTransform,
                            cancellationToken,
                            _captureProtection);
                    }
                    if (playerTransform is { } chosenPlayerTransform)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsCurrentMatchOperation(operationMatch))
                        {
                            trace?.SetTerminal("superseded", "match-operation-version-changed");
                            return;
                        }
                        recognition = WithOverlayTransform(
                            recognition,
                            chosenPlayerTransform);
                        playerDecidedScale = true;
                        _logCollector.Append(
                            MapLogCategory.Session,
                            MapLogLevel.Info,
                            $"玩家已决定缩放值 · map={recognition.Map.Id} · "
                            + $"floor={recognition.Result.Floor} · "
                            + $"scale={chosenPlayerTransform.ScaleX:F6} · "
                            + $"offset=({chosenPlayerTransform.OffsetX:F0},"
                            + $"{chosenPlayerTransform.OffsetY:F0})");
                    }
                }
                _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);
                var adaptiveScale = trace?.StartTopLevel(
                    "adaptive_scale_evaluation",
                    MapOperationWaitKind.Compute,
                    mapId: recognition.Map.Id.ToString("D"),
                    floorKey: recognition.Result.Floor);
                AdaptiveAlignmentDecision adaptiveDecision;
                try
                {
                    adaptiveDecision = await EvaluateAdaptiveInitialAsync(
                        recognition,
                        frame,
                        _lastDiagnostics,
                        playerDecidedScale ? MapFeatureCacheSource.Player : null);
                }
                finally
                {
                    adaptiveScale?.Complete();
                }
                recognition = adaptiveDecision.RecognitionToRender;
                // 画面就绪参考签名写入不受 AllowLegacyCacheWrite 门控：provisional
                // 也记录，否则下次开图「仅对齐」就绪判定缺 reference 走 blue-gray
                // 兜底分支（首帧必拒、白付抓帧周期）。
                RememberMapViewportPresenceReference(recognition, frame);
                if (adaptiveDecision.AllowLegacyCacheWrite)
                {
                    var persistence = trace?.StartTopLevel(
                        "persistence",
                        MapOperationWaitKind.Io,
                        mapId: recognition.Map.Id.ToString("D"),
                        floorKey: recognition.Result.Floor);
                    try
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
                    finally
                    {
                        persistence?.Complete();
                    }
                }
                if (!IsCurrentMatchOperation(operationMatch))
                {
                    trace?.SetTerminal("superseded", "match-operation-version-changed");
                    return;
                }
                var sessionCommit = trace?.StartTopLevel(
                    "session_commit",
                    MapOperationWaitKind.Compute,
                    mapId: recognition.Map.Id.ToString("D"),
                    floorKey: recognition.Result.Floor);
                try
                {
                    _lastRecognition = recognition;
                    _mapLease.Bind(_matchSession.Snapshot, recognition.Map.Id);
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
                    }
                    RememberReliableFloorAlignment(
                        operationMatch,
                        recognition,
                        _lastAlignmentSession,
                        frame);
                    _lastGameBounds = frame.ClientBounds;
                    _lastGameWindowHandle = frame.WindowHandle;
                }
                finally
                {
                    sessionCommit?.Complete();
                }

                var present = _overlay.DeferPresent();
                var overlayPublish = trace?.StartTopLevel(
                    "overlay_publish",
                    MapOperationWaitKind.Compute,
                    mapId: recognition.Map.Id.ToString("D"),
                    floorKey: recognition.Result.Floor);
                try
                {
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
                    _logCollector.Append(
                        MapLogCategory.Overlay,
                        MapLogLevel.Info,
                        $"Overlay 更新完成 · visible={_overlay.IsVisible} · hasMap={_overlay.HasMap}");
                    if (adaptiveDecision.StartOrbTracking)
                    {
                        var orbTracking = trace?.StartChild(
                            "orb_tracking",
                            MapOperationWaitKind.Compute,
                            mapId: recognition.Map.Id.ToString("D"),
                            floorKey: recognition.Result.Floor);
                        try
                        {
                            await StartOrbTrackingAsync(recognition, frame);
                        }
                        finally
                        {
                            orbTracking?.Complete();
                        }
                    }
                    // 识别成功后刷新持久小地图（若启用）
                    var miniMapPublish = trace?.StartChild(
                        "mini_map_publish",
                        MapOperationWaitKind.Compute,
                        mapId: recognition.Map.Id.ToString("D"),
                        floorKey: recognition.Result.Floor);
                    try
                    {
                        RefreshMiniMapForCurrentFloor();
                    }
                    finally
                    {
                        miniMapPublish?.Complete();
                    }
                    _scanProgressOverlay.Report(0.98d, "正在完成...");
                }
                finally
                {
                    present.Dispose();
                    overlayPublish?.Complete();
                }
            }
            else if (failureReason is not null)
            {
                trace?.SetTerminal("failed", "recognition-failed");
                _statusMessage = failureReason;
                _logCollector.Append(
                    MapLogCategory.ScanLifecycle,
                    MapLogLevel.Warning,
                    _statusMessage);
                var failurePresent = _overlay.DeferPresent();
                var failureOverlay = trace?.StartTopLevel(
                    "overlay_publish",
                    MapOperationWaitKind.Compute,
                    mapId: _lastRecognition?.Map.Id.ToString("D"),
                    floorKey: _lastRecognition?.Result.Floor);
                try
                {
                    ShowTransientOverlayStatus(
                        MapOverlayStatusLevel.Failure,
                        "Overlay 识别失败",
                        failureReason,
                        "请查看日志中的扫描、对齐和 Overlay 记录。",
                        frame.ClientBounds,
                        frame.WindowHandle);
                    var failureMiniMap = trace?.StartChild(
                        "mini_map_publish",
                        MapOperationWaitKind.Compute,
                        mapId: _lastRecognition?.Map.Id.ToString("D"),
                        floorKey: _lastRecognition?.Result.Floor);
                    try
                    {
                        RefreshMiniMapForCurrentFloor();
                    }
                    finally
                    {
                        failureMiniMap?.Complete();
                    }
                }
                finally
                {
                    failurePresent.Dispose();
                    failureOverlay?.Complete();
                }
            }
            else
            {
                trace?.SetTerminal("failed", "recognition-produced-no-result");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            trace?.SetTerminal("failed", $"exception:{ex.GetType().Name}");
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
            var exceptionPresent = _overlay.DeferPresent();
            var exceptionOverlay = trace?.StartTopLevel(
                "overlay_publish",
                MapOperationWaitKind.Compute,
                mapId: _lastRecognition?.Map.Id.ToString("D"),
                floorKey: _lastRecognition?.Result.Floor);
            try
            {
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
                exceptionPresent.Dispose();
                exceptionOverlay?.Complete();
            }
        }
        finally
        {
            var frameDispose = ActiveOperationTrace?.StartChild(
                "frame_dispose",
                MapOperationWaitKind.Io,
                mapId: frame is null ? null : _lastRecognition?.Map.Id.ToString("D"),
                floorKey: _lastRecognition?.Result.Floor);
            try
            {
                frame?.Dispose();
            }
            finally
            {
                frameDispose?.Complete();
            }
        }
    }
}
/*
 * 文件职责：SessionOrchestrator.Pipeline.Recognition。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
