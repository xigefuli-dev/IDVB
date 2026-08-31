// IDVB Remaster — 后台扫描（Background Scan）开图消费
// 玩家第一次打开游戏地图时，消费后台扫描保存的识别结果：
// 候选（如有）→ 缩放（如有）→ 尝试一次对齐，然后按标准仅对齐流程提交。

using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

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
}
