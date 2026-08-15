using OpenCvSharp;
using System.Diagnostics;

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

        var prioritizeStructureValidation =
            MapOpenAlignmentRouteRules.ShouldPrioritizeStructureValidation(
                route,
                MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is not null);
        var stopwatch = Stopwatch.StartNew();
        using var liveMatchImage = prioritizeStructureValidation
            ? new Mat()
            : GateTemplateDetector.CreateMatchImage(frame.Image);
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

        var gateResult = prioritizeStructureValidation
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
        var gates = gateResult.Gates;
        stopwatch.Stop();
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
                gateResult = service.GateDetector.Detect(
                    liveMatchImage,
                    frame.ViewportBounds,
                    frame.ClientBounds.Width,
                    tuning.GateTemplateThreshold,
                    warmContext);
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
        using var reference = Cv2.ImRead(
            fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
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
