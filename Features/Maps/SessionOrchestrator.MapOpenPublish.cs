using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private enum MapOpenAlignmentPublishOutcome
    {
        Succeeded,
        Failed,
        Superseded
    }

    private async Task<MapOpenAlignmentPublishOutcome>
        PublishMapOpenAlignmentResultAsync(
        MapGameToggleTransition toggle,
        MapMatchSnapshot operationMatch,
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string targetFloorKey,
        bool recoveringSelectedIdentity,
        RuntimeMapRecognition? aligned,
        string? failureReason,
        MapFeatureCacheKey? repairCacheKey,
        bool resetRecoveredScaleState)
    {
        var trace = ActiveOperationTrace;
        var independentAlignment = string.Equals(
            _lastDiagnostics?.WarmStateMissReason,
            "independent-alignment",
            StringComparison.Ordinal);
        if (independentAlignment
            && aligned?.Result.ReusedLastTransform is true)
        {
            aligned = null;
            failureReason = "独立对齐未通过，已拒绝直接复用上次变换。";
        }
        var resultPublish = trace?.StartTopLevel(
            "result_publish",
            MapOperationWaitKind.Compute,
            mapId: locked.Map.Id.ToString("D"),
            floorKey: targetFloorKey);
        try
        {
        // A background result must never overwrite a newer close/open action.
        if (!IsCurrentMatchOperation(operationMatch)
            || !_gameMapToggleState.IsCurrent(toggle))
        {
            resultPublish?.Complete(
                MapOperationSpanStatus.Superseded,
                "map-operation-version-changed");
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "仅对齐结果已丢弃（地图已关闭或重新打开）。");
            return MapOpenAlignmentPublishOutcome.Superseded;
        }

        if (aligned is not null)
        {
            if (resetRecoveredScaleState)
            {
                var recoveredContextKey = CreateAlignmentContextKey(
                    operationMatch,
                    frame,
                    aligned.Map,
                    targetFloorKey);
                ForgetReliableFloorAlignment(recoveredContextKey);
                await ResetAdaptiveScaleAfterSteadyRecoveryAsync(
                    frame,
                    aligned.Map,
                    targetFloorKey);
            }
            // 在 adaptive 仲裁改写 transform 之前记录：这里 aligned 是结构配准在
            // 就绪帧上找出的真实对齐，locked 是上次成功对齐。二者位移差就是
            // 「重开图漂移」——决定投影边界掩膜能否复用上次位移的关键证据。
            LogMapOpenOffsetDrift(locked, aligned, targetFloorKey);
            var adaptiveDecision = await EvaluateAdaptiveInitialAsync(
                aligned,
                frame,
                _lastDiagnostics);
            if (_lastDiagnostics is { } adaptiveDiagnostics
                && MapAlignmentChannelRegistry.Resolve(
                    aligned.Map,
                    aligned.Result.Floor).Channel == MapAlignmentChannel.LowStructure)
            {
                adaptiveDiagnostics.LowStructureEvidenceCount =
                    adaptiveDecision.ConsecutiveHighQualityCount;
                adaptiveDiagnostics.LowStructureEvidenceRequired =
                    adaptiveDecision.RequiredHighQualityCount;
                adaptiveDiagnostics.LowStructureEvidencePending =
                    adaptiveDecision.ConsecutiveHighQualityCount
                        < adaptiveDecision.RequiredHighQualityCount;
                adaptiveDiagnostics.LowStructureScaleRelativeMad =
                    adaptiveDecision.InitialScaleRelativeMad;
                adaptiveDiagnostics.LowStructureEvidenceRebuildReason =
                    adaptiveDecision.InitialScaleClusterRebuilt
                        ? "scale-outside-quantized-basin"
                        : string.Empty;
            }
            aligned = adaptiveDecision.RecognitionToRender;
            resultPublish?.Complete();
            if (!IsCurrentMatchOperation(operationMatch)
                || !_gameMapToggleState.IsCurrent(toggle))
            {
                trace?.SetTerminal("superseded", "match-operation-version-changed");
                return MapOpenAlignmentPublishOutcome.Superseded;
            }

            // 画面就绪参考签名不再受 AllowLegacyCacheWrite 门控：provisional（尺度
            // 缓存未达 reliable）也应记录本次对齐帧的颜色签名，否则下次开图「仅对齐」
            // 就绪判定会因缺少 reference 落到 blue-gray 兜底分支，该分支首帧必拒、
            // 白付一个抓帧周期。签名只依赖就绪帧画面，与 scale 是否 reliable 无关。
            RememberMapViewportPresenceReference(aligned, frame);
            if (adaptiveDecision.AllowLegacyCacheWrite)
                RecordSuccessfulAlignment(aligned, frame);

            // 与识别管线一致：首次成功对齐时 _lastAlignmentSession 可能仍为
            // null，需回退到侧门扫描种子以保留 SideEntranceScanPriorConfidence；
            // 否则仅对齐成功后先验归零，后续重新对齐会退化到 Default 双门路线。
            var sideEntranceSeed = _pendingAlignmentSeed;
            var sessionCommit = trace?.StartTopLevel(
                "session_commit",
                MapOperationWaitKind.Compute,
                mapId: aligned.Map.Id.ToString("D"),
                floorKey: aligned.Result.Floor);
            try
            {
                if (aligned.Result.OverlayTransform is { } committedTransform)
                {
                    _mapOpenSession.LockAlignedMap(
                        aligned.Map.Id,
                        aligned.Result.Floor,
                        MapSimilarityTransform.FromOverlay(committedTransform),
                        aligned.Result.EvidenceKind switch
                        {
                            MapAlignmentEvidenceKind.DualGate => MapLocationMethod.DualAnchor,
                            MapAlignmentEvidenceKind.SingleGateAndAuxiliary => MapLocationMethod.SingleAnchor,
                            MapAlignmentEvidenceKind.AuxiliaryConsensus => MapLocationMethod.AuxiliaryAnchor,
                            MapAlignmentEvidenceKind.Structure => MapLocationMethod.StructureTranslation,
                            _ => MapLocationMethod.Manual
                        },
                        aligned.Result.LocalizationConfidence);
                }
                _lastRecognition = aligned;
                _mapLease.Bind(_matchSession.Snapshot, aligned.Map.Id);
                _pendingAlignmentIdentity = null;
                _pendingAlignmentSeed = null;
                var updatedSession = UpdateAlignmentSession(
                    _lastAlignmentSession ?? sideEntranceSeed,
                    aligned);
                _lastAlignmentSession = updatedSession;
                if (adaptiveDecision.AllowReliableSession)
                {
                    RememberPrimaryFloorSession(aligned, updatedSession);
                }
                RememberReliableFloorAlignment(
                    operationMatch,
                    aligned,
                    updatedSession,
                    frame,
                    adaptiveDecision.AllowReliableSession);
                _lastGameBounds = frame.ClientBounds;
                _lastGameWindowHandle = frame.WindowHandle;
                _statusMessage =
                    $"地图已对齐：{aligned.Map.DisplayName} · {aligned.Result.Floor.ToUpperInvariant()}";
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    $"仅对齐完成 · map={aligned.Map.Id} · floor={aligned.Result.Floor}",
                    details: new()
                    {
                        ["mapId"] = aligned.Map.Id,
                        ["floor"] = aligned.Result.Floor,
                        ["identityConfidence"] =
                            aligned.Result.IdentityConfidence,
                        ["localizationConfidence"] =
                            aligned.Result.LocalizationConfidence,
                        ["candidateMargin"] =
                            MapFeatureCacheRules.GetCandidateMargin(aligned.Result)
                    });
            }
            finally
            {
                sessionCommit?.Complete();
            }
            var skipPresent = ShouldSkipPresentDueToOptimisticMatch(
                aligned,
                toggle.Version,
                out _,
                out _);

            var overlayPublish = trace?.StartTopLevel(
                "overlay_publish",
                MapOperationWaitKind.Compute,
                mapId: aligned.Map.Id.ToString("D"),
                floorKey: aligned.Result.Floor);
            try
            {
                if (skipPresent)
                {
                    var finalPresent = trace?.StartChild(
                        "final_present",
                        MapOperationWaitKind.Compute,
                        mapId: aligned.Map.Id.ToString("D"),
                        floorKey: aligned.Result.Floor);
                    finalPresent?.Complete(
                        MapOperationSpanStatus.Skipped,
                        "optimistic-exact-match");
                }
                else
                {
                    var present = _overlay.DeferPresent();
                    try
                    {
                        _overlay.SetMainContentVisible(true);
                        if (!_overlay.TryUpdateMapTransformOnly(
                                aligned,
                                frame.ClientBounds,
                                frame.WindowHandle,
                                frame.ViewportBounds))
                        {
                            _overlay.UpdateMap(
                                aligned,
                                frame.ClientBounds,
                                frame.WindowHandle,
                                _settings!.ShowOverlayStatus,
                                frame.ViewportBounds);
                        }
                        if (adaptiveDecision.AllowReliableSession)
                        {
                            ShowAdaptiveReliableStatus(
                                aligned,
                                adaptiveDecision,
                                frame.ClientBounds,
                                frame.WindowHandle);
                        }
                        else
                        {
                            ShowAdaptiveProvisionalStatus(
                                aligned,
                                adaptiveDecision,
                                frame.ClientBounds,
                                frame.WindowHandle);
                        }
                        _overlay.Show();
                    }
                    finally
                    {
                        var finalPresent = trace?.StartChild(
                            "final_present",
                            MapOperationWaitKind.Compute,
                            mapId: aligned.Map.Id.ToString("D"),
                            floorKey: aligned.Result.Floor);
                        try
                        {
                            present.Dispose();
                        }
                        finally
                        {
                            finalPresent?.Complete();
                        }
                    }
                }
            }
            finally
            {
                overlayPublish?.Complete();
            }

            PublishMiniMapAfterMainPresent(aligned, aligned.Result.Floor, false);

            // Rendering is the latency boundary visible to the user. Tracking
            // startup and cache I/O must not delay the final Present call.
            if (adaptiveDecision.StartOrbTracking && !independentAlignment)
            {
                var trackingStart = trace?.StartTopLevel(
                    "tracking_start",
                    MapOperationWaitKind.Compute,
                    mapId: aligned.Map.Id.ToString("D"),
                    floorKey: aligned.Result.Floor);
                try
                {
                    await StartOrbTrackingAsync(aligned, frame);
                }
                finally
                {
                    trackingStart?.Complete();
                }
            }
            if (adaptiveDecision.AllowLegacyCacheWrite)
            {
                var persistence = trace?.StartTopLevel(
                    "persistence",
                    MapOperationWaitKind.Io,
                    mapId: aligned.Map.Id.ToString("D"),
                    floorKey: aligned.Result.Floor);
                try
                {
                    await RepairMapCacheAsync(repairCacheKey, aligned, frame);
                    await PersistPreprocessedScaleAsync(
                        aligned,
                        frame,
                        _lastDiagnostics);
                }
                finally
                {
                    persistence?.Complete();
                }
            }
            if (MapAlignmentChannelRegistry.Resolve(
                    aligned.Map,
                    aligned.Result.Floor).Channel
                == MapAlignmentChannel.LowStructure
                && _lastDiagnostics is { } lowDiagnostics)
            {
                await PersistLowStructureScaleAsync(
                    aligned,
                    frame,
                    lowDiagnostics);
            }
            return MapOpenAlignmentPublishOutcome.Succeeded;
        }

        // A failed alignment clears the stale transform instead of presenting
        // the previous image as though the new observation had succeeded.
        var manualFloorLabel = MapFloorRules.GetFloorDisplayName(
            locked.Map,
            targetFloorKey);
        var pendingVariant = IsPendingVariantAlignment(
            locked.Map.Id,
            targetFloorKey);
        _statusMessage = recoveringSelectedIdentity
            ? $"所选地图暂未完成首次对齐：{locked.Map.DisplayName} · "
                + $"{failureReason ?? "无法匹配当前画面"}"
            : $"对齐未更新：当前按{manualFloorLabel}对齐；"
                + $"{failureReason ?? "无法匹配当前画面"}";
        resultPublish?.Complete();
        _logCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Warning,
            $"仅对齐未更新 · map={locked.Map.Id} · reason={failureReason ?? "<none>"}");
        var failurePresent = _overlay.DeferPresent();
        _overlay.SetMainContentVisible(true);
        var overlayPublishFailure = trace?.StartTopLevel(
            "overlay_publish",
            MapOperationWaitKind.Compute,
            mapId: locked.Map.Id.ToString("D"),
            floorKey: targetFloorKey);
        try
        {
            _overlay.ClearMap();
            var isPendingWait = pendingVariant || recoveringSelectedIdentity;
            ShowTransientOverlayStatus(
                isPendingWait
                    ? MapOverlayStatusLevel.Warning
                    : MapOverlayStatusLevel.Failure,
                isPendingWait
                    ? (pendingVariant ? "目标变体等待对齐" : "已选定地图，等待开图对齐")
                    : "地图重新对齐失败",
                _statusMessage,
                isPendingWait
                    ? (pendingVariant
                        ? "目标地图身份和楼层已保留；本次没有复用旧变体的覆盖层或变换。"
                        : "已锁定所选地图身份；请保持完整地图打开并重新打开地图以完成对齐。")
                    : "本次未复用旧变换；请保持完整地图打开，确认 IDVB 手动楼层正确后重新打开地图重试。",
                frame.ClientBounds,
                frame.WindowHandle);
            _overlay.Show();
            RestorePendingVariantStatusAfterTransient(
                _statusMessage,
                locked,
                targetFloorKey);
        }
        finally
        {
            var finalPresent = trace?.StartChild(
                "final_present",
                MapOperationWaitKind.Compute,
                mapId: locked.Map.Id.ToString("D"),
                floorKey: targetFloorKey);
            try
            {
                failurePresent.Dispose();
            }
            finally
            {
                finalPresent?.Complete();
            }
            overlayPublishFailure?.Complete();
        }
        PublishMiniMapAfterMainPresent(locked, targetFloorKey, true);
        return MapOpenAlignmentPublishOutcome.Failed;
        }
        finally
        {
            resultPublish?.Complete();
        }
    }

    /// <summary>
    /// 记录「重开图漂移」：上次成功对齐的位移 vs 本次就绪帧结构配准找出的位移。
    /// 位移偏差按屏幕像素计（投影边界掩膜正是在 viewport 像素下投影）。聚合该
    /// 偏差分布即可决定能否安全复用上次位移做投影掩膜（edges 52→15ms）与局部
    /// 平移搜索——偏差小则安全，偏差大则破坏性掩膜会切掉真实地图结构。
    /// </summary>
    private void LogMapOpenOffsetDrift(
        RuntimeMapRecognition locked,
        RuntimeMapRecognition aligned,
        string targetFloorKey)
    {
        if (!MapOpenAlignmentRouteRules.CanCompareMapOpenDrift(
                locked,
                aligned,
                targetFloorKey))
        {
            return;
        }

        var previous = locked.Result.OverlayTransform;
        var current = aligned.Result.OverlayTransform;
        if (previous is null
            || current is null
            || !double.IsFinite(previous.OffsetX)
            || !double.IsFinite(previous.OffsetY)
            || !double.IsFinite(current.OffsetX)
            || !double.IsFinite(current.OffsetY))
        {
            // 首次对齐（身份锁定但变换未就绪）没有可比的旧位移，跳过。
            return;
        }

        var deltaX = current.OffsetX - previous.OffsetX;
        var deltaY = current.OffsetY - previous.OffsetY;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        var scaleDelta = double.IsFinite(previous.ScaleX)
                && previous.ScaleX > 0d
                && double.IsFinite(current.ScaleX)
            ? (current.ScaleX / previous.ScaleX) - 1d
            : double.NaN;

        _logCollector.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"重开图偏移漂移 · dx={deltaX:F1} dy={deltaY:F1} dist={distance:F1}px "
            + $"· scaleΔ={scaleDelta:P2}",
            details: new()
            {
                ["mapId"] = locked.Map.Id,
                ["floor"] = targetFloorKey,
                ["offsetDeltaPixels"] = distance,
                ["offsetDeltaX"] = deltaX,
                ["offsetDeltaY"] = deltaY,
                ["previousOffsetX"] = previous.OffsetX,
                ["previousOffsetY"] = previous.OffsetY,
                ["currentOffsetX"] = current.OffsetX,
                ["currentOffsetY"] = current.OffsetY,
                ["previousScale"] = previous.ScaleX,
                ["currentScale"] = current.ScaleX,
                ["scaleDeltaRatio"] = scaleDelta
            });
    }
}
/*
 * 文件职责：SessionOrchestrator.MapOpenPublish。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
