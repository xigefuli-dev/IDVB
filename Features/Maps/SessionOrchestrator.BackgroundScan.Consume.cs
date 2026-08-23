// IDVB Remaster — 后台扫描（Background Scan）开图消费
// 玩家第一次打开游戏地图时，消费后台扫描保存的识别结果：
// 候选（如有）→ 缩放（如有）→ 尝试一次对齐，然后按标准仅对齐流程提交。

using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    /// <summary>
    /// 公开缝合点（供测试 / CLI）：当存在未消费的后台扫描结果且游戏地图处于
    /// 打开状态时，立即消费一次。headless 下候选窗自动选可靠项、缩放窗跳过。
    /// </summary>
    public async Task ConsumeBackgroundScanAsync()
    {
        if (!IsBackgroundScanCompleted || !_gameMapToggleState.IsOpen)
            return;
        // 不翻转开图状态：外部控制器已把地图置为打开，仅取当前 open 的快照。
        var toggle = _gameMapToggleState.SetOpenForExternalController(true);
        await ConsumeBackgroundScanAsync(toggle);
    }

    private async Task ConsumeBackgroundScanAsync(MapGameToggleTransition toggle)
    {
        var mapOpenCancellation = BeginMapOpenCancellationScope();
        var cancellationToken = mapOpenCancellation.Token;
        CancelOrbTracking("background scan consume started");
        await DrainOrbTrackingAsync();
        var operationMatch = _matchSession.Snapshot;
        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有识别正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            CompleteMapOpenCancellationScope(mapOpenCancellation);
            return;
        }

        var trace = BeginMapOperationTrace(
            MapOperationTypes.CandidateConfirmation,
            CandidateConfirmationTracePhases);
        var outcome = "success";
        var terminalReason = "completed";
        var traceFinished = false;
        var restoreOverlay = _overlay.IsVisible;
        using (trace.StartTopLevel("route_prepare"))
        {
            if (restoreOverlay)
                _overlay.Hide();
        }
        try
        {
            await ConsumeBackgroundScanCoreAsync(
                toggle,
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"后台扫描消费已取消 · matchVersion={operationMatch.Version}");
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
                {
                    if (restoreOverlay
                        && IsCurrentMatchOperation(operationMatch)
                        && _gameMapToggleState.IsCurrent(toggle)
                        && !_overlay.IsVisible)
                    {
                        _overlay.Show();
                    }
                    _scanGate.Release();
                }
            }
            finally
            {
                CompleteMapOpenCancellationScope(mapOpenCancellation);
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

    private async Task ConsumeBackgroundScanCoreAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        // ── 候选确认：仅歧义 / 强制候选场景需要玩家从候选列表选择 ──
        RuntimeMapRecognition? locked = null;
        CapturedGameFrame? candidateFrame = null;
        if (_pendingBackgroundChoices is { Count: > 0 })
        {
            // 候选卡片和识别区预览均在后台扫描阶段已就绪；禁止在开图事件
            // 上等待稳定帧，否则首个开图无法直接显示候选界面。
            candidateFrame = _pendingBackgroundCandidateFrame;
            if (candidateFrame is null)
            {
                ActiveOperationTrace?.SetTerminal(
                    "failed",
                    "background-candidate-preview-not-ready");
                _statusMessage =
                    "后台扫描候选预览尚未就绪，请重新按快捷扫描键。";
                return;
            }

            // 此路径的预览已由后台扫描预热，若立刻 Activate 候选窗口，窗口
            // 可能赶在游戏处理开图热键前抢走焦点。仅 GUI 候选窗等待一次很短
            // 的输入交接；headless 与显式候选选择器不创建窗口，无需等待。
            if (BackgroundScanRules.ShouldWaitForCandidateInputHandoff(
                    _headless,
                    _activeCandidateSelector is not null))
            {
                await Task.Delay(
                    BackgroundScanRules.CandidatePresentationInputHandoffMilliseconds,
                    cancellationToken);
                if (!IsCurrentMatchOperation(operationMatch)
                    || !_gameMapToggleState.IsCurrent(toggle))
                {
                    return;
                }
            }

            var candidateSelection = ActiveOperationTrace?.StartTopLevel(
                "candidate_selection_wait",
                MapOperationWaitKind.User,
                mapId: locked?.Map.Id.ToString("D"),
                floorKey: locked?.Result.Floor);
            CandidateSelectionResolution resolution;
            try
            {
                resolution = await ResolveCandidateSelectionAsync(
                    candidateFrame,
                    _pendingBackgroundChoices,
                    _pendingBackgroundChoicesReason,
                    operationMatch.MapClass!,
                    cancellationToken,
                    _pendingBackgroundChoicesAreDisplayReady,
                    _pendingBackgroundChoicePreviews,
                    _pendingBackgroundLivePreview);
            }
            finally
            {
                candidateSelection?.Complete();
            }
            if (resolution.StartSurvey)
            {
                // 用户转入测绘：后台结果已被取代，作废待消费状态。
                await ActivateSurveyFromQuickScanAsync(
                    candidateFrame,
                    operationMatch,
                    cancellationToken);
                return;
            }
            locked = resolution.Recognition;
            if (locked is null)
            {
                ActiveOperationTrace?.SetTerminal("failed", "candidate-not-confirmed");
                _statusMessage = "后台扫描候选未确认，已放弃本次消费。";
                ClearPendingBackgroundScan();
                return;
            }
        }
        else
        {
            locked = _pendingBackgroundIdentity;
        }

        if (locked is null)
        {
            ActiveOperationTrace?.SetTerminal("failed", "background-scan-had-no-identity");
            _statusMessage = "后台扫描未识别出地图，请重新按快捷扫描键。";
            ClearPendingBackgroundScan();
            return;
        }

        ActiveOperationTrace?.SetContext(
            mapId: locked.Map.Id.ToString("D"),
            floorKey: ResolveBackgroundConsumeFloorKey(locked));

        // 歧义路径（候选确认）下后台扫描不产单一侧门种子：像前台候选确认
        // （Recognition.cs:123-255）一样，用已保存的侧门扫描结果为选中的候选
        // 重建侧门种子（SideEntranceScanPriorConfidence>0），否则配对回退
        // KEEP-1.0 兜底（prior=0）后消费对齐会走双门路径而失败。
        // 确定性单候选路径 seed 已由扫描产出，无需重建。
        if (_pendingBackgroundSeed is null
            && _pendingBackgroundScan is { } pendingScan
            && candidateFrame is not null)
        {
            var selectedCandidate = pendingScan.Candidates
                .FirstOrDefault(candidate => candidate.Map.Id == locked.Map.Id);
            if (selectedCandidate is not null
                && _recognition.TryCreateSideEntranceAlignmentSeed(
                    selectedCandidate,
                    candidateFrame.ViewportBounds,
                    out var rebuiltSeed,
                    out _))
            {
                _pendingBackgroundSeed = rebuiltSeed;
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    $"后台扫描候选已确认，为选中的候选重建侧门种子 · "
                    + $"map={locked.Map.DisplayName} · floor={locked.Result.Floor}");
            }
        }

        // 身份锁定：与身份配对种子，保证任何退出路径都不会残留
        // seedless 身份 → 下次开图 RunMapOpenAlignmentCoreAsync 会对 null 变换
        // 调用 MapAlignmentSession.FromRecognition 崩溃（seedless 雷区）。
        // 侧门策略下优先使用后台扫描保存的真实侧门种子（SideEntranceScanPriorConfidence>0），
        // 使消费对齐自动切到侧门路由；否则回退独立楼层种子（KEEP-1.0）走 Default 路由。
        // 对齐锁建立后立即清空后台字段并置 Idle：后台结果已移交对齐链路，
        // 失败的后续重试走 _pendingAlignmentIdentity / _pendingAlignmentSeed。
        var targetFloorKey = ResolveBackgroundConsumeFloorKey(locked);
        var sideEntranceSeed = BackgroundScanRules.PickSideEntranceSeed(
            _pendingBackgroundSeed,
            locked,
            targetFloorKey);
        // Reliable chooser entries passed strict structure validation during
        // background recognition. Preserve their content-derived scale across
        // candidate confirmation, but never publish the old preview-frame
        // translation; the current map frame receives one unrestricted-
        // translation registration below.
        var validatedStructureScaleSeed =
            BackgroundScanRules.BuildValidatedStructureScaleSeed(
                locked,
                sideEntranceSeed,
                targetFloorKey);
        _pendingAlignmentIdentity = locked;
        _mapLease.Bind(_matchSession.Snapshot, locked.Map.Id);
        _pendingAlignmentSeed = validatedStructureScaleSeed
            ?? sideEntranceSeed
            ?? CreateIndependentFloorSeedSession(
                locked,
                targetFloorKey);
        if (validatedStructureScaleSeed is not null)
        {
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                "后台候选严格验证尺度已保留；当前帧仅重算平移",
                details: new()
                {
                    ["mapId"] = locked.Map.Id,
                    ["floor"] = targetFloorKey,
                    ["scale"] = validatedStructureScaleSeed.LockedTransform.ScaleX,
                    ["oldOffsetX"] =
                        validatedStructureScaleSeed.LockedTransform.OffsetX,
                    ["oldOffsetY"] =
                        validatedStructureScaleSeed.LockedTransform.OffsetY,
                    ["translationPolicy"] = "unrestricted-current-frame"
                });
        }
        ClearPendingBackgroundScan();

        await RunBackgroundConsumeAlignmentAsync(
            toggle,
            operationMatch,
            frame: null,
            locked,
            targetFloorKey,
            validatedStructureScaleSeed,
            cancellationToken);
    }

    private async Task RunBackgroundConsumeAlignmentAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        CapturedGameFrame? frame,
        RuntimeMapRecognition locked,
        string targetFloorKey,
        MapAlignmentSession? validatedStructureScaleSeed,
        CancellationToken cancellationToken)
    {
        // 候选预览帧只用于立即展示候选窗口；真实对齐必须在用户选中后
        // 重新捕获当前完整地图帧。
        var ownsFrame = frame is null;
        if (frame is null)
        {
            var stableViewport = ActiveOperationTrace?.StartTopLevel(
                "stable_viewport",
                MapOperationWaitKind.Capture);
            try
            {
                frame = await CaptureStableViewportAsync(
                    "后台扫描消费对齐",
                    cancellationToken);
            }
            finally
            {
                stableViewport?.Complete();
            }
        }
        if (frame is null)
        {
            ActiveOperationTrace?.SetTerminal(
                "failed",
                "stable-viewport-capture-failed");
            // 身份锁与 seed 已配对保留，玩家重新打开地图可经 recovering 链路重试。
            _statusMessage =
                string.IsNullOrWhiteSpace(_lastStableCaptureFailureReason)
                    ? "后台扫描消费：地图截图失败，请保持地图打开后重试。"
                    : _lastStableCaptureFailureReason;
            _logCollector.Append(
                MapLogCategory.ViewportCapture,
                MapLogLevel.Warning,
                _statusMessage);
            return;
        }

        try
        {
            // 与正常扫描一致的初始对齐入口：真实侧门种子走带完整恢复上下文
            // 的侧门路线，其余主层才走通用 selected-map 路线；非主层保持
            // no-door 精确楼层路线。
            var alignmentTuning = CreateInitialAlignmentRecognitionTuning();
            if (alignmentTuning.GateTemplateThreshold
                > GateTemplateRules.FallbackPairThreshold)
            {
                alignmentTuning.GateTemplateThreshold =
                    GateTemplateRules.FallbackPairThreshold;
            }
            MapRecognitionAttempt AlignFor(
                RuntimeMapRecognition identity,
                out MapFeatureCacheKey? repair)
            {
                repair = null;
                var mapId = identity.Map.Id;
                var structureTuning = CreateStructureTuningForFloor(
                    identity.Map,
                    targetFloorKey,
                    CreateInitialAlignmentStructureTuning());
                // A genuine side-entrance seed must enter the same initial
                // side-route recovery used by foreground candidate confirmation.
                // Passing it through generic AlignSelectedCore without a search
                // context detects gate glyphs but cannot run the unrestricted
                // side recovery, so alignment only starts working after a map
                // variant switch reconstructs that context.
                var sideEntranceSeed = BackgroundScanRules.PickSideEntranceSeed(
                    _pendingAlignmentSeed,
                    identity,
                    targetFloorKey);
                MapRecognitionAttempt Align()
                {
                    if (!string.Equals(
                            targetFloorKey,
                            MapFloorRules.GetPrimaryFloorKey(identity.Map),
                            StringComparison.Ordinal))
                    {
                        return _recognition.AlignFloorWithoutGates(
                            frame,
                            mapId,
                            targetFloorKey,
                            MapFloorScaleSeedRules.CreateIndependentFloorSeed(
                                identity.Map,
                                targetFloorKey),
                            _settings!.OverlayAlignmentMode,
                            alignmentTuning,
                            structureTuning,
                            allowPrimaryFloor: false);
                    }

                    if (sideEntranceSeed is not null)
                    {
                        return _recognition.AlignSideEntrance(
                            frame,
                            mapId,
                            sideEntranceSeed,
                            _settings!.OverlayAlignmentMode,
                            alignmentTuning,
                            structureTuning,
                            alignmentSearchContext:
                                CreateSideEntranceSearchContext(
                                    sideEntranceSeed,
                                    alignmentTuning,
                                    useInitialHighPrecisionRecovery: true));
                    }

                    return MapCvAlignmentService.AlignSelectedCore(
                        _recognition, frame, mapId,
                        session: null,
                        alignmentMode: _settings!.OverlayAlignmentMode,
                        tuning: alignmentTuning,
                        structureTuning: structureTuning,
                        playerPrior: null, predictedViewportOrigin: null,
                        liveIgnoreRegions: null, candidateHistory: null,
                        alignmentSearchContext: null,
                        nativeScaleChangeRatio: 1.0,
                        mapClass: null,
                        route: SelectedAlignmentRoute.Default);
                }
                if (_recognition.TryGetMap(mapId) is not { } selectedMap)
                    return Align();

                if (MapOpenAlignmentRouteRules.IsCompatibleReliableFloorSession(
                        validatedStructureScaleSeed,
                        selectedMap.Id,
                        selectedMap.UpdatedAt,
                        targetFloorKey,
                        minimumConfidence: 0d))
                {
                    var fixedScaleAttempt = _recognition.AlignWithCachedScale(
                        frame,
                        mapId,
                        targetFloorKey,
                        validatedStructureScaleSeed!.LockedTransform,
                        _settings!.OverlayAlignmentMode,
                        alignmentTuning,
                        structureTuning,
                        identityPriorConfidence:
                            identity.Result.IdentityConfidence,
                        restrictTranslationToSeed: false);
                    if (fixedScaleAttempt.Recognition is not null)
                        return fixedScaleAttempt;

                    _logCollector.Append(
                        MapLogCategory.StructureRegistration,
                        MapLogLevel.Warning,
                        "后台候选验证尺度在当前帧未通过，进入内容尺度恢复",
                        details: new()
                        {
                            ["mapId"] = mapId,
                            ["floor"] = targetFloorKey,
                            ["scale"] = validatedStructureScaleSeed
                                .LockedTransform.ScaleX,
                            ["failureReason"] =
                                fixedScaleAttempt.FailureReason,
                            ["rejectionReason"] = fixedScaleAttempt
                                .StructureResult?.RejectionReason.ToString()
                        });
                }

                return AlignUsingScaleCache(
                    frame,
                    selectedMap,
                    targetFloorKey,
                    alignmentTuning,
                    structureTuning,
                    0d,
                    Align,
                    out repair);
            }

            MapFeatureCacheKey? repairCacheKey = null;
            var selectedAlignment = ActiveOperationTrace?.StartTopLevel(
                "selected_candidate_alignment",
                MapOperationWaitKind.Compute,
                mapId: locked.Map.Id.ToString("D"),
                floorKey: targetFloorKey,
                attemptIndex: 0);
            var selectedDispatch = MapOperationTraceAmbient.StartChild(
                "candidate_dispatch_wait",
                MapOperationWaitKind.Queue,
                mapId: locked.Map.Id.ToString("D"),
                floorKey: targetFloorKey,
                attemptIndex: 0);
            MapRecognitionAttempt attempt;
            try
            {
                attempt = await Task.Run(
                    () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var alignmentDeadline = new NoDoorAlignmentDeadline(
                            cancellationToken,
                            MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds,
                            enforceTimeBudget: false);
                        using var alignmentBudget = alignmentDeadline.EnterAmbient();
                        selectedDispatch.Complete();
                        using var selectedWorker = MapOperationTraceAmbient.StartChild(
                            "candidate_worker_execution",
                            MapOperationWaitKind.Compute,
                            mapId: locked.Map.Id.ToString("D"),
                            floorKey: targetFloorKey,
                            attemptIndex: 0);
                        return AlignFor(locked, out repairCacheKey);
                    },
                    cancellationToken);
            }
            finally
            {
                selectedDispatch.Complete();
                selectedAlignment?.Complete();
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch)
                || !_gameMapToggleState.IsCurrent(toggle))
            {
                ActiveOperationTrace?.SetTerminal(
                    "superseded",
                    "map-operation-version-changed");
                return;
            }
            _lastDiagnostics = attempt.Diagnostics;

            var aligned = attempt.Recognition;
            var failureReason = attempt.FailureReason;

            // ── 二次候选：对齐本身产生歧义时让玩家确认后，用所选地图再对齐一次 ──
            if (aligned is null && attempt.Choices.Count > 0)
            {
                var candidateSelection = ActiveOperationTrace?.StartTopLevel(
                    "candidate_selection_wait",
                    MapOperationWaitKind.User,
                    mapId: locked.Map.Id.ToString("D"),
                    floorKey: targetFloorKey);
                CandidateSelectionResolution resolution;
                try
                {
                    resolution = await ResolveCandidateSelectionAsync(
                        frame,
                        attempt.Choices,
                        failureReason ?? string.Empty,
                        operationMatch.MapClass!,
                        cancellationToken);
                }
                finally
                {
                    candidateSelection?.Complete();
                }
                if (resolution.StartSurvey)
                {
                    ActiveOperationTrace?.SetTerminal("superseded", "survey-started");
                    await ActivateSurveyFromQuickScanAsync(
                        frame,
                        operationMatch,
                        cancellationToken);
                    return;
                }
                if (resolution.Recognition is not { } chosen)
                {
                    ActiveOperationTrace?.SetTerminal(
                        "cancelled",
                        "candidate-selection-cancelled");
                    // 用户取消：保留配对身份锁，下次开图可重试对齐。
                    _statusMessage =
                        "后台扫描消费：候选未确认，已保留地图身份，本次未对齐。";
                    return;
                }

                locked = chosen;
                targetFloorKey = ResolveBackgroundConsumeFloorKey(chosen);
                ActiveOperationTrace?.SetContext(
                    mapId: chosen.Map.Id.ToString("D"),
                    floorKey: targetFloorKey);
                // 二次对齐选中的地图成为新的锁定身份（供失败重试与状态展示），
                // 并重新配对楼层种子，避免恢复链路与 AlignFor 使用与地图不匹配
                // 的 seed。优先保留侧门种子，否则回退独立楼层种子。
                _pendingAlignmentIdentity = chosen;
                _mapLease.Bind(_matchSession.Snapshot, chosen.Map.Id);
                _pendingAlignmentSeed = BackgroundScanRules.PickSideEntranceSeed(
                        _pendingAlignmentSeed,
                        chosen,
                        targetFloorKey)
                    ?? CreateIndependentFloorSeedSession(chosen, targetFloorKey);
                MapFeatureCacheKey? secondRepairKey = null;
                var secondAlignment = ActiveOperationTrace?.StartTopLevel(
                    "selected_candidate_alignment",
                    MapOperationWaitKind.Compute,
                    mapId: chosen.Map.Id.ToString("D"),
                    floorKey: targetFloorKey,
                    attemptIndex: 1);
                var secondDispatch = MapOperationTraceAmbient.StartChild(
                    "candidate_dispatch_wait",
                    MapOperationWaitKind.Queue,
                    mapId: chosen.Map.Id.ToString("D"),
                    floorKey: targetFloorKey,
                    attemptIndex: 1);
                MapRecognitionAttempt secondAttempt;
                try
                {
                    secondAttempt = await Task.Run(
                        () =>
                        {
                            secondDispatch.Complete();
                            using var secondWorker = MapOperationTraceAmbient.StartChild(
                                "candidate_worker_execution",
                                MapOperationWaitKind.Compute,
                                mapId: chosen.Map.Id.ToString("D"),
                                floorKey: targetFloorKey,
                                attemptIndex: 1);
                            return AlignFor(chosen, out secondRepairKey);
                        },
                        cancellationToken);
                }
                finally
                {
                    secondDispatch.Complete();
                    secondAlignment?.Complete();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentMatchOperation(operationMatch)
                    || !_gameMapToggleState.IsCurrent(toggle))
                {
                    ActiveOperationTrace?.SetTerminal(
                        "superseded",
                        "map-operation-version-changed");
                    return;
                }
                aligned = secondAttempt.Recognition;
                failureReason = secondAttempt.FailureReason;
                repairCacheKey = secondRepairKey;
                _lastDiagnostics = secondAttempt.Diagnostics;
            }

            // ── 由玩家决定缩放值：确认后直接以玩家 transform 渲染 ──
            if (aligned is { } alignedRecognition
                && _settings!.RecognitionTuning.PlayerDecidesScale
                && alignedRecognition.Result.OverlayTransform is { } initialTransform)
            {
                MapOverlayTransform? playerTransform;
                using (ActiveOperationTrace?.StartTopLevel(
                           "candidate_selection_wait",
                           MapOperationWaitKind.User,
                           mapId: alignedRecognition.Map.Id.ToString("D"),
                           floorKey: alignedRecognition.Result.Floor)
                    ?? MapOperationTrace.MapOperationSpanScope.Noop)
                {
                    playerTransform = await MapManualTransformWindow.ShowAsync(
                        frame,
                        alignedRecognition,
                        initialTransform,
                        cancellationToken,
                        _captureProtection);
                }
                if (playerTransform is { } chosenPlayerTransform)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentMatchOperation(operationMatch)
                        || !_gameMapToggleState.IsCurrent(toggle))
                    {
                        ActiveOperationTrace?.SetTerminal(
                            "superseded",
                            "map-operation-version-changed");
                        return;
                    }
                    alignedRecognition = WithOverlayTransform(
                        alignedRecognition,
                        chosenPlayerTransform);
                    aligned = alignedRecognition;
                }
            }

            var publishOutcome = await PublishMapOpenAlignmentResultAsync(
                toggle,
                operationMatch,
                frame,
                locked,
                targetFloorKey,
                recoveringSelectedIdentity: true,
                aligned,
                failureReason,
                repairCacheKey,
                resetRecoveredScaleState: false);
            if (publishOutcome == MapOpenAlignmentPublishOutcome.Superseded)
            {
                ActiveOperationTrace?.SetTerminal(
                    "superseded",
                    "map-operation-version-changed");
                return;
            }
            if (publishOutcome == MapOpenAlignmentPublishOutcome.Failed
                || aligned is null)
            {
                ActiveOperationTrace?.SetTerminal(
                    "failed",
                    publishOutcome == MapOpenAlignmentPublishOutcome.Failed
                        ? "alignment-not-accepted"
                        : "alignment-result-not-committed");
                return;
            }

            // 成功：首次完整对齐被认可，允许自动地图缓存写入。
            _hasCompletedQuickScanAlignment = true;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            // 取消由外层 ConsumeBackgroundScanAsync(toggle) 统一记录；
            // 配对身份锁保留，供重新开图经 recovering 链路重试。
            throw;
        }
        catch (Exception ex)
        {
            ActiveOperationTrace?.SetTerminal("failed", $"exception:{ex.GetType().Name}");
            // 与 RunMapOpenAlignmentCoreAsync 一致：对齐/提交异常不冒泡到
            // 输入处理，记录后保留配对身份锁供下次开图重试。
            _statusMessage = $"后台扫描消费异常：{ex.Message}";
            _logCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Error,
                _statusMessage,
                details: new()
                {
                    ["exceptionType"] = ex.GetType().FullName,
                    ["stackTrace"] = ex.ToString()
                });
        }
        finally
        {
            if (ownsFrame)
                DisposeBackgroundFrame(
                    frame,
                    locked.Map.Id.ToString("D"),
                    targetFloorKey);
        }
    }

    private void DisposeBackgroundFrame(
        CapturedGameFrame? frame,
        string? mapId = null,
        string? floorKey = null)
    {
        if (frame is null)
            return;

        var cleanup = ActiveOperationTrace?.StartTopLevel(
            "cleanup",
            MapOperationWaitKind.Io,
            mapId: mapId,
            floorKey: floorKey);
        try
        {
            var dispose = MapOperationTraceAmbient.StartChild(
                "frame_dispose",
                MapOperationWaitKind.Io,
                mapId: mapId,
                floorKey: floorKey);
            try
            {
                frame.Dispose();
            }
            finally
            {
                dispose.Complete();
            }
        }
        finally
        {
            cleanup?.Complete();
        }
    }

    private static string ResolveBackgroundConsumeFloorKey(
        RuntimeMapRecognition locked)
    {
        var floorKey = locked.Result.Floor;
        if (MapFloorRules.GetFloorProfile(locked.Map, floorKey) is null)
            floorKey = MapFloorRules.GetPrimaryFloorKey(locked.Map);
        return floorKey;
    }

    private static MapAlignmentSession CreateIndependentFloorSeedSession(
        RuntimeMapRecognition locked,
        string floorKey)
    {
        var transform = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
            locked.Map,
            floorKey);
        return new MapAlignmentSession
        {
            MapId = locked.Map.Id,
            MapUpdatedAt = locked.Map.UpdatedAt,
            FloorKey = floorKey,
            LockedTransform = transform,
            BaselineGateScale = transform.ScaleX,
            HasGatePairLock = false,
            Mode = MapAlignmentTrackingMode.GatePairLocked,
            SideEntranceScanPriorConfidence = 0d
        };
    }
}
/*
 * 文件职责：SessionOrchestrator.BackgroundScan.Consume。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
