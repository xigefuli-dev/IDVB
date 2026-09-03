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
        try
        {
            // This open event owns the latest map operation. The predecessor
            // has already been cancelled above; wait for its cleanup instead
            // of losing the background-result consumption in the release gap.
            await _scanGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CompleteMapOpenCancellationScope(mapOpenCancellation);
            return;
        }
        if (!IsCurrentMatchOperation(operationMatch)
            || !_gameMapToggleState.IsCurrent(toggle))
        {
            _scanGate.Release();
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
        RuntimeMapRecognition? verifiedAlignment = null;
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
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    "开图事件正在显示已冻结的候选结果。",
                    details: new()
                    {
                        ["candidateCount"] = _pendingBackgroundChoices.Count,
                        ["modelVersion"] =
                            _pendingBackgroundLearningResult?.ModelVersion
                                ?? string.Empty,
                        ["modelScoringOnOpen"] = false
                    });
                resolution = await ResolveCandidateSelectionAsync(
                    candidateFrame,
                    _pendingBackgroundChoices,
                    _pendingBackgroundChoicesReason,
                    operationMatch.MapClass!,
                    cancellationToken,
                    _pendingBackgroundChoicesAreDisplayReady,
                    _pendingBackgroundChoicePreviews,
                    _pendingBackgroundLivePreview,
                    _pendingBackgroundLearningResult);
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
            verifiedAlignment = resolution.VerifiedAlignment;
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
                verifiedAlignment ?? locked,
                sideEntranceSeed,
                targetFloorKey);
        var selectedIdentitySource = validatedStructureScaleSeed is not null
            ? "verified-structure"
            : sideEntranceSeed is not null
                ? "scan-side-seed"
                : "catalog-identity-only";
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            "后台消费首次对齐路由已确定",
            details: new()
            {
                ["selected_identity_source"] = selectedIdentitySource,
                ["initial_alignment_route"] = MapOpenAlignmentRouteRules
                    .ResolveInitialAlignmentRoute(
                        validatedStructureScaleSeed is not null,
                        sideEntranceSeed is not null),
                ["mapId"] = locked.Map.Id,
                ["floor"] = targetFloorKey,
                ["hasValidatedStructureScaleSeed"] =
                    validatedStructureScaleSeed is not null,
                ["hasSideEntranceSeed"] = sideEntranceSeed is not null
            });
        _pendingAlignmentIdentity = locked;
        _currentFloorKey = targetFloorKey;
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
}
/*
 * 文件职责：SessionOrchestrator.BackgroundScan.Consume。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
