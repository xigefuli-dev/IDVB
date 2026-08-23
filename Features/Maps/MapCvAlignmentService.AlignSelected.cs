using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    internal static MapRecognitionAttempt AlignSelectedCore(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapAlignmentSession? session,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        AlignmentSearchContext? alignmentSearchContext,
        double nativeScaleChangeRatio,
        string? mapClass,
        SelectedAlignmentRoute route)
    {
        ObjectDisposedException.ThrowIf(service.IsDisposed, service);

        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        tuning.ForceBestRecognitionResult = false;
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        structureTuning ??= new MapStructureRegistrationTuning();
        structureTuning = structureTuning.Clone();
        structureTuning.Normalize();
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            service.ReadyMapCount,
            service.TotalMapCount);
        using var selectedRoute = MapOperationTraceAmbient.StartChild(
            "selected_alignment_route",
            MapOperationWaitKind.Compute,
            mapId: selectedMapId.ToString("D"));
        var searchCtx = alignmentSearchContext;

        diagnostics.SearchStage =
            searchCtx?.GateSearch.Mode switch
            {
                GateSearchMode.FullSearch => AlignmentSearchStage.FullGateSearch,
                GateSearchMode.WarmScaleSearch => AlignmentSearchStage.WarmGateSearch,
                GateSearchMode.LockedScale => AlignmentSearchStage.LockedGateSearch,
                GateSearchMode.LocalConfirmationSearch =>
                    AlignmentSearchStage.LocalGateConfirmation,
                _ => AlignmentSearchStage.None,
            };

        var fingerprint = service.FilterFingerprints(mapClass).FirstOrDefault(
            candidate => candidate.Map.Id == selectedMapId);
        if (fingerprint is null)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "当前选择的地图不存在或尚未完成主层区域与双门标记；地图序号没有被删除。");
        }

        var resolvedChannel = MapAlignmentChannelRegistry.Resolve(
            fingerprint.Map,
            fingerprint.FloorKey);
        if (structureTuning.Channel != resolvedChannel.Channel)
        {
            diagnostics.AlignmentChannel = structureTuning.Channel ==
                MapAlignmentChannel.LowStructure
                    ? MapAlignmentChannelRegistry.LowStructure.DiagnosticLabel
                    : MapAlignmentChannelRegistry.Standard.DiagnosticLabel;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"Floor '{fingerprint.FloorKey}' requires alignment channel "
                + $"'{resolvedChannel.DiagnosticLabel}', but received "
                + $"'{diagnostics.AlignmentChannel}'.");
        }
        diagnostics.AlignmentChannel = resolvedChannel.DiagnosticLabel;
        diagnostics.FloorMarkerKeys = string.Join(
            ",",
            MapFloorMarkerRules.Normalize(
                MapFloorRules.GetOrderedFloors(fingerprint.Map)
                    .First(floor => string.Equals(
                        floor.Key,
                        fingerprint.FloorKey,
                        StringComparison.Ordinal))
                    .MarkerKeys));
        diagnostics.AlignmentConfigFingerprint =
            resolvedChannel.Channel == MapAlignmentChannel.LowStructure
                ? structureTuning.CacheFingerprint
                : "legacy";

        var compatibleSession = session is not null
            && session.MapId == selectedMapId
            && session.MapUpdatedAt == fingerprint.Map.UpdatedAt
            && session.LockedTransform.AlignmentMode == alignmentMode
                ? session
                : null;

        // A side-entrance identity is a match-level invariant. Protect the
        // lower-level generic API as well as the orchestrator so an accidental
        // AlignSelected call cannot reinterpret two detected glyphs as a
        // default dual-gate lock.
        if (route == SelectedAlignmentRoute.Default
            && compatibleSession is
            {
                SideEntranceScanPriorConfidence: > 0d,
                HasGatePairLock: false
            })
        {
            route = SelectedAlignmentRoute.SideEntrance;
        }

        if (route == SelectedAlignmentRoute.SideEntrance
            && (compatibleSession is null
                || compatibleSession.SideEntranceScanPriorConfidence <= 0d
                || compatibleSession.HasGatePairLock))
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "侧门对齐缺少当前地图的侧门扫描种子；请重新执行侧门扫描。");
        }

        var hasAlignmentDeadline =
            MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is not null;
        var prioritizeStructureValidation =
            MapOpenAlignmentRouteRules.ShouldPrioritizeStructureValidation(
                route,
                hasAlignmentDeadline)
            // 兜底已锁定身份：无门楼层不需要门检测。双门 RankGeometry 只用于
            // 身份选择，此处会话已匹配当前地图；实测本场景门检测 290 次从未
            // 找到双门、仅 14 次找到单个门且还要过身份确认门槛，白白付出
            // 150ms+。有 NoDoor 预算且会话匹配时直接走结构配准，跳过
            // CreateMatchImage + FullSearch/WarmScaleSearch。
            || (route == SelectedAlignmentRoute.Default
                && compatibleSession is not null
                && hasAlignmentDeadline);
        var stopwatch = Stopwatch.StartNew();
        using var inputPreprocess = MapOperationTraceAmbient.StartChild(
            "alignment_input_preprocess",
            MapOperationWaitKind.Compute,
            mapId: selectedMapId.ToString("D"),
            floorKey: fingerprint.FloorKey);
        using var liveMatchImage = prioritizeStructureValidation
            ? new Mat()
            : GateTemplateDetector.CreateMatchImage(frame.Image);
        inputPreprocess.Complete();
        stopwatch.Stop();
        diagnostics.PreprocessMilliseconds = prioritizeStructureValidation
            ? 0d
            : stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        var gateContext = searchCtx?.GateSearch
            ?? new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
            };

        GateDetectionResult gateResult;
        using (var gateDetection = MapOperationTraceAmbient.StartChild(
                   "alignment_gate_detection",
                   MapOperationWaitKind.Compute,
                   mapId: selectedMapId.ToString("D"),
                   floorKey: fingerprint.FloorKey))
        {
            gateResult = prioritizeStructureValidation
                ? new GateDetectionResult
                {
                    SearchModeUsed = gateContext.Mode,
                    StopReason = GateSearchStopReason.Completed,
                }
                : service.GateDetector.Detect(
                    liveMatchImage,
                    frame.ViewportBounds,
                    frame.ClientBounds.Width,
                    tuning.GateTemplateThreshold,
                    gateContext);
            if (prioritizeStructureValidation)
            {
                gateDetection.Complete(
                    MapOperationSpanStatus.Skipped,
                    "identity-locked-structure-priority");
            }
        }
        var gates = gateResult.Gates;
        stopwatch.Stop();
        // 记录兜底已锁定身份时的门检测跳过（区别于侧门路由的既有跳过），
        // 便于从日志验证影响面与后续结构配准成功率。
        if (prioritizeStructureValidation
            && route == SelectedAlignmentRoute.Default)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.GateDetection,
                MapLogLevel.Info,
                $"兜底已锁定身份，跳过门检测直接结构配准 · floor={fingerprint.FloorKey}",
                elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                details: new()
                {
                    ["mapId"] = selectedMapId,
                    ["floor"] = fingerprint.FloorKey,
                    ["hasDeadline"] = hasAlignmentDeadline,
                    ["sessionMatched"] = compatibleSession is not null,
                    ["searchMode"] = gateContext.Mode.ToString(),
                });
        }
        diagnostics.GateDetectionMilliseconds = prioritizeStructureValidation
            ? 0d
            : stopwatch.Elapsed.TotalMilliseconds;

        // ── LockedScale safety net ───────────────────────────────────────────────
        if (!prioritizeStructureValidation
            && gateContext.Mode == GateSearchMode.LockedScale
            && gateContext.LockedScale is { } lockedScale)
        {
            var lockedGoodEnough = gates.Count >= 2
                || (gates.Count == 1
                    && gates[0].Score >= tuning.GateTemplateThreshold
                        + GateTemplateRules.SingleGateAmbiguityGap
                    && Math.Abs((gates[0].Scale / lockedScale) - 1d) <= 0.12d);

            if (!lockedGoodEnough)
            {
                var warmContext = new GateSearchContext
                {
                    Mode = GateSearchMode.WarmScaleSearch,
                    WarmScale = lockedScale,
                    AllowSingleGateEarlyExit = true,
                    SingleGateScoreThreshold =
                        GateTemplateRules.EarlyExitScoreThreshold,
                    SingleGateScaleTolerance =
                        GateTemplateRules.SingleGateScaleTolerance,
                    AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
                };
                if (tuning.WarmGateSearchBudgetMs > 0)
                    warmContext.TimeBudgetMilliseconds =
                        tuning.WarmGateSearchBudgetMs;

                stopwatch.Restart();
                using (var warmGate = MapOperationTraceAmbient.StartChild(
                           "warm_gate_detection",
                           MapOperationWaitKind.Compute,
                           mapId: selectedMapId.ToString("D"),
                           floorKey: fingerprint.FloorKey))
                {
                    gateResult = service.GateDetector.Detect(
                        liveMatchImage,
                        frame.ViewportBounds,
                        frame.ClientBounds.Width,
                        tuning.GateTemplateThreshold,
                        warmContext);
                }
                gates = gateResult.Gates;
                stopwatch.Stop();
                diagnostics.GateDetectionMilliseconds =
                    stopwatch.Elapsed.TotalMilliseconds;

                MapLogCollector.Instance.Append(
                    MapLogCategory.GateDetection,
                    MapLogLevel.Warning,
                    $"LockedScale 单 scale 搜索未提供合格的门候选 " +
                    $"(找到 {gates.Count} 个)，回退到 WarmScaleSearch",
                    elapsedMs: diagnostics.GateDetectionMilliseconds,
                    details: new()
                    {
                        ["fallbackFrom"] = "LockedScale",
                        ["fallbackTo"] = "WarmScaleSearch",
                        ["lockedScale"] = lockedScale,
                        ["gateCount"] = gates.Count,
                    });
            }
        }

        MapCvRecognitionHelpers.PopulateGateDiagnosticsAndIgnoreRegions(
            diagnostics, gateResult, gates, frame, out var dynamicIgnoreRegions);

        if (gates.Count >= 2 && route == SelectedAlignmentRoute.Default)
        {
            return AlignSelectedWithGatePair(
                service,
                frame,
                fingerprint,
                compatibleSession,
                alignmentMode,
                tuning,
                structureTuning,
                playerPrior,
                predictedViewportOrigin,
                liveIgnoreRegions,
                dynamicIgnoreRegions,
                candidateHistory,
                nativeScaleChangeRatio,
                diagnostics,
                stopwatch,
                gateResult,
                gates);
        }

        if (compatibleSession is null)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"已保留 {fingerprint.Map.DisplayName}，但本次运行尚未完成双门缩放锁定；"
                + "请让大门和侧门同时出现在地图显示边界内一次。");
        }

        session = compatibleSession;

        stopwatch.Restart();
        using var referenceLoad = MapOperationTraceAmbient.StartChild(
            "reference_image_load",
            MapOperationWaitKind.Io,
            mapId: selectedMapId.ToString("D"),
            floorKey: fingerprint.FloorKey);
        using var reference = Cv2.ImRead(
            fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
        referenceLoad.Complete();
        stopwatch.Stop();
        diagnostics.ReferenceImageLoadMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;
        if (reference.Empty())
        {
            if (tuning.ForceBestRecognitionResult)
            {
                return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                    fingerprint,
                    session,
                    diagnostics);
            }

            diagnostics.TrackingMode = MapAlignmentTrackingMode.WaitingForAnchor;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "无法读取当前所选地图的识别区域。");
        }

        var singleGateFailure = TryPrepareSelectedSingleGate(
            service,
            frame,
            fingerprint,
            session,
            tuning,
            route,
            searchCtx,
            reference,
            gates,
            diagnostics,
            stopwatch,
            out var singleGateFallbackReason,
            out var singleGateProposal,
            out var freshAnchorTransform,
            out var structureSeed);
        if (singleGateFailure is not null)
            return singleGateFailure;

        if (alignmentMode != MapOverlayAlignmentMode.Uniform)
        {
            if (tuning.ForceBestRecognitionResult)
            {
                return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                    fingerprint,
                    session,
                    diagnostics);
            }

            diagnostics.TrackingMode = MapAlignmentTrackingMode.HoldingLastTransform;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                (singleGateFallbackReason is null
                    ? "两扇门都不可见"
                    : $"{singleGateFallbackReason}；单门无法安全更新平移")
                + "，而结构配准只支持等比缩放；当前 XY 分别缩放模式已保留上次对齐。");
        }
        return AlignSelectedWithStructure(
            service,
            frame,
            fingerprint,
            session,
            tuning,
            structureTuning,
            route,
            searchCtx,
            playerPrior,
            predictedViewportOrigin,
            liveIgnoreRegions,
            candidateHistory,
            gates,
            gateResult,
            diagnostics,
            reference,
            dynamicIgnoreRegions,
            singleGateFallbackReason,
            singleGateProposal,
            freshAnchorTransform,
            structureSeed,
            stopwatch);
    }

}
/*
 * 文件职责：MapCvAlignmentService.AlignSelected。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
