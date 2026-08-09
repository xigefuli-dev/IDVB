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

        var stopwatch = Stopwatch.StartNew();
        using var liveMatchImage = GateTemplateDetector.CreateMatchImage(frame.Image);
        stopwatch.Stop();
        diagnostics.PreprocessMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        var gateContext = searchCtx?.GateSearch
            ?? new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
            };

        var gateResult = service.GateDetector.Detect(
            liveMatchImage,
            frame.ViewportBounds,
            frame.ClientBounds.Width,
            tuning.GateTemplateThreshold,
            gateContext);
        var gates = gateResult.Gates;
        stopwatch.Stop();
        diagnostics.GateDetectionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        // ── LockedScale safety net ───────────────────────────────────────────────
        if (gateContext.Mode == GateSearchMode.LockedScale
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
            stopwatch.Restart();
            var ranked = MapCvRecognitionScript.RankGeometry(
                [fingerprint],
                gates,
                frame.ViewportBounds,
                tuning.VectorErrorTolerance);
            stopwatch.Stop();
            diagnostics.GeometryMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

            if (!MapCvRecognitionDiagnostics.TryValidateRanking(
                    ranked, tuning, diagnostics, out var failure))
            {
                if (tuning.ForceBestRecognitionResult
                    && compatibleSession is not null)
                {
                    return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                        fingerprint,
                        compatibleSession,
                        diagnostics);
                }

                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return failure!;
            }

            var winner = ranked[0];
            if (!MapCvRecognitionBuilders.TryBuildRecognition(
                    winner,
                    alignmentMode,
                    tuning,
                    margin: double.PositiveInfinity,
                    usedConfirmation: false,
                    MapRecognitionSource.SelectedMapGatePair,
                    wasForcedBestResult: false,
                    out var recognition,
                    out var transformFailure))
            {
                if (tuning.ForceBestRecognitionResult
                    && compatibleSession is not null)
                {
                    return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                        fingerprint,
                        compatibleSession,
                        diagnostics);
                }

                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"双门与已选地图一致，但无法安全对齐覆盖层：{transformFailure}");
            }

            if (recognition!.Result.Confidence < tuning.MinimumConfidence
                && !tuning.ForceBestRecognitionResult)
            {
                diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"已选地图的双门对齐置信度 {recognition.Result.Confidence:P0} "
                    + $"低于阈值 {tuning.MinimumConfidence:P0}。");
            }

            if (compatibleSession is not null
                && recognition.Result.OverlayTransform is { } measured)
            {
                var scaleChange = Math.Abs(
                    (measured.ScaleX
                        / compatibleSession.LockedTransform.ScaleX) - 1d);
                if (scaleChange > nativeScaleChangeRatio)
                {
                    diagnostics.TrackingMode =
                        MapAlignmentTrackingMode.NeedsGatePair;
                    diagnostics.StructureRejectionReason =
                        MapStructureRejectionReason.NativeScaleChanged;
                    return MapCvRecognitionDiagnostics.Failure(
                        diagnostics,
                        $"双门测得的原生地图缩放与固定标定相差超过 "
                        + $"{nativeScaleChangeRatio:P0}，"
                        + "本次结果已拒绝，需要重新确认地图缩放。");
                }

                if (MapOverlayTransformSolver.TryTranslateWithLockedScale(
                        compatibleSession.LockedTransform,
                        recognition.Result.AnchorMatches,
                        out var fixedScaleTransform,
                        out _))
                {
                    recognition = MapCvRecognitionBuilders.ReplaceTransform(
                        recognition,
                        fixedScaleTransform);
                }
            }

            if (MapCvRecognitionBuilders.CanDirectLockGatePair(recognition, tuning))
            {
                recognition = MapCvRecognitionBuilders.MarkFastEvidence(
                    recognition,
                    MapAlignmentEvidenceKind.DualGate,
                    MapStructureEvidenceDisposition.None,
                    skippedStructure: true);
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "双门快速锁定，跳过结构复核");

                service.GateDetector.RememberSuccessfulScale(
                    (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
                diagnostics.UsedForcedBestResult = false;
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.GatePairLocked;
                diagnostics.AlignmentEvidence =
                    MapAlignmentEvidenceKind.DualGate;
                diagnostics.SkippedStructureValidation = true;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Recognition = recognition,
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                };
            }

            if (!MapCvAlignmentService.TryValidateAnchorRecognitionWithStructure(
                    service,
                    fingerprint,
                    frame,
                    recognition,
                    structureTuning,
                    tuning.MinimumConfidence,
                    playerPrior,
                    predictedViewportOrigin,
                    liveIgnoreRegions,
                    dynamicIgnoreRegions,
                    candidateHistory,
                    out var validatedRecognition,
                    out var anchorStructure,
                    out var structureFailure))
            {
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.NeedsGatePair;
                diagnostics.StructureRejectionReason =
                    anchorStructure?.RejectionReason
                    ?? MapStructureRejectionReason.NoCandidate;
                diagnostics.StructureDisposition =
                    diagnostics.StructureRejectionReason.ToDisposition();
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"双门几何已匹配，但静态结构与地图边界复核失败：{structureFailure}");
            }

            recognition = validatedRecognition;

            if (anchorStructure is not null)
            {
                diagnostics.StructurePreprocessMilliseconds =
                    anchorStructure.PreprocessMilliseconds;
                diagnostics.StructureSearchMilliseconds =
                    anchorStructure.SearchMilliseconds;
                diagnostics.StructureRefineMilliseconds =
                    anchorStructure.RefineMilliseconds;
                diagnostics.StructureBestScore = anchorStructure.BestScore;
                diagnostics.StructureSecondScore = anchorStructure.SecondScore;
                diagnostics.StructureCandidateMargin =
                    anchorStructure.CandidateMargin;
                diagnostics.StructureRejectionReason =
                    anchorStructure.RejectionReason;
                diagnostics.StructureDisposition =
                    anchorStructure.RejectionReason.ToDisposition(
                        anchorStructure.Accepted);
                diagnostics.AlignmentEvidence =
                    MapAlignmentEvidenceKind.Structure;
                PopulateStructureDiagnostics(
                    diagnostics,
                    anchorStructure);
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"结构复核：{(anchorStructure.Accepted ? "通过" : "未通过")} · 置信度 {anchorStructure.Confidence:P0}",
                    elapsedMs: anchorStructure.SearchMilliseconds
                        + anchorStructure.RefineMilliseconds,
                    details: new()
                    {
                        ["accepted"] = anchorStructure.Accepted,
                        ["confidence"] = anchorStructure.Confidence,
                        ["bestScore"] = anchorStructure.BestScore,
                    ["rejectionReason"] = anchorStructure.RejectionReason.ToString()
                    });
            }

            service.GateDetector.RememberSuccessfulScale(
                (winner.MainGate.Scale + winner.SideGate.Scale) / 2d);
            diagnostics.UsedForcedBestResult =
                recognition.Result.WasForcedBestResult;
            diagnostics.TrackingMode = MapAlignmentTrackingMode.GatePairLocked;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                Recognition = recognition,
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
            };
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

        using var reference = Cv2.ImRead(
            fingerprint.RecognitionImagePath,
            ImreadModes.Unchanged);
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

        string? singleGateFallbackReason = null;
        RuntimeMapRecognition? singleGateProposal = null;
        MapOverlayTransform? freshAnchorTransform = null;
        var structureSeed = session.LockedTransform;
        GateDetection? singleGate = gates.Count == 1 ? gates[0] : null;

        // The side-entrance route deliberately never promotes a frame with
        // two visible gates into a dual-gate alignment. Such a frame uses the
        // selected map's structure only; a single-gate proposal is allowed
        // only when this frame contains exactly one detectable gate.

        if (singleGate is { } gate)
        {
            if (session.GateTemplateScale is { } lockedGateScale
                && Math.Abs((gate.Scale / lockedGateScale) - 1d) > 0.12d)
            {
                if (route == SelectedAlignmentRoute.SideEntrance)
                {
                    // A side scan gate is a useful proposal only while its
                    // template scale agrees with the locked scan evidence.
                    // It must not block the independent structure-only path.
                    singleGate = null;
                    singleGateFallbackReason =
                        "侧门单门缩放与扫描证据不一致，转入仅结构配准";
                }
                else if (tuning.ForceBestRecognitionResult)
                {
                    return MapCvRecognitionBuilders.ReuseLastTransformAttempt(
                        fingerprint,
                        session,
                        diagnostics);
                }

                else
                {
                    diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                    return MapCvRecognitionDiagnostics.Failure(
                        diagnostics,
                        "单门尺寸与已锁定缩放不一致，可能发生了地图缩放；请等待双门重新锁定。");
                }
            }

            if (singleGate is not null)
            {
                if (session.SideEntranceScanPriorConfidence > 0d)
                {
                    if (searchCtx?.UseRestrictedStructureFallback == true)
                    {
                        // This is the same frame that produced the side-feature
                        // match. Its gate identity, scale and translation have
                        // already been measured together, so rerunning the
                        // generic single-gate classifier is redundant and can
                        // reject sparse but valid side-entrance screenshots.
                        singleGateProposal =
                            MapCvRecognitionBuilders.BuildTrackedRecognition(
                                fingerprint,
                                session.LockedTransform,
                                session.LockedGateEvidence,
                                MapRecognitionSource.SideEntranceSelection,
                                confidenceOverride:
                                    session.SideEntranceScanPriorConfidence,
                                evidenceKind: MapAlignmentEvidenceKind.None);
                        structureSeed = session.LockedTransform;
                    }
                    else
                    {
                    // A later frame may expose the other gate. Re-identify the
                    // single visible gate against the locked map; if it cannot be
                    // identified safely, continue with structure-only alignment.
                    var seProfile = MapFloorRules.GetFloorProfile(
                        fingerprint.Map,
                        fingerprint.FloorKey)
                        ?? fingerprint.Map.Recognition.FirstFloor;
                    var sideAnchorId = seProfile.FindAnchor("side-entrance")?.Id;
                    stopwatch.Restart();
                    var resolved = MapAnchorTracker.TryResolveSingleGate(
                        reference,
                        frame.Image,
                        fingerprint,
                        gate,
                        frame.ViewportBounds,
                        session.LockedTransform,
                        tuning.MinimumConfidence,
                        tuning.ConfirmationAdvantage,
                        out var evidence,
                        out var identityFailure);
                    stopwatch.Stop();
                    diagnostics.ConfirmationMilliseconds =
                        stopwatch.Elapsed.TotalMilliseconds;

                    if (!resolved || sideAnchorId is null
                        || evidence.AnchorId != sideAnchorId.Value)
                    {
                        singleGateFallbackReason = string.IsNullOrWhiteSpace(identityFailure)
                            ? "侧门链路无法确认当前单门身份，转入仅结构配准"
                            : $"侧门单门身份确认失败：{identityFailure}";
                    }
                    else if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                                 session.LockedTransform,
                                 [evidence],
                                 out var seTransform,
                                 out var seTransformFailure))
                    {
                        singleGateFallbackReason =
                            $"侧门单门平移失败：{seTransformFailure}";
                    }
                    else
                    {
                        diagnostics.TrackingMode =
                            MapAlignmentTrackingMode.SingleGateTracking;
                        var scaleAgreement = MapAlignmentConfidence
                            .ComputeScaleAgreement(
                                gate.Scale,
                                session.GateTemplateScale
                                    ?? (GateTemplateRules.ReferenceScale
                                        * session.BaselineGateScale));
                        var singleGateConfidence = MapAlignmentConfidence
                            .ComputeSideEntranceSingleGateConfidence(
                                session.SideEntranceScanPriorConfidence,
                                evidence.Score,
                                scaleAgreement);
                        singleGateProposal =
                            MapCvRecognitionBuilders.BuildTrackedRecognition(
                                fingerprint,
                                seTransform,
                                [evidence],
                                MapRecognitionSource.SingleGateTracking,
                                confidenceOverride: singleGateConfidence,
                                evidenceKind: MapAlignmentEvidenceKind.None);
                        structureSeed = seTransform;
                        freshAnchorTransform = seTransform;
                    }
                    }
                }
                else
                {
                    stopwatch.Restart();
                    var resolved = MapAnchorTracker.TryResolveSingleGate(
                        reference,
                        frame.Image,
                        fingerprint,
                        gate,
                        frame.ViewportBounds,
                        session.LockedTransform,
                        tuning.MinimumConfidence,
                        tuning.ConfirmationAdvantage,
                        out var evidence,
                        out var identityFailure);
                    stopwatch.Stop();
                    diagnostics.ConfirmationMilliseconds =
                        stopwatch.Elapsed.TotalMilliseconds;
                    MapLogCollector.Instance.Append(
                        MapLogCategory.GateDetection,
                        MapLogLevel.Info,
                        $"单门身份识别{(resolved ? "成功" : "失败")} · {stopwatch.Elapsed.TotalMilliseconds:F0}ms",
                        elapsedMs: stopwatch.Elapsed.TotalMilliseconds);

                    if (!resolved)
                    {
                        singleGateFallbackReason = identityFailure;
                    }
                    else if (!MapOverlayTransformSolver.TryTranslateWithLockedScale(
                                 session.LockedTransform,
                                 [evidence],
                                 out var transform,
                                 out var transformFailure))
                    {
                        singleGateFallbackReason = transformFailure;
                    }
                    else
                    {
                        diagnostics.TrackingMode =
                            MapAlignmentTrackingMode.SingleGateTracking;

                        var scaleAgreement = MapAlignmentConfidence.ComputeScaleAgreement(
                            gate.Scale,
                            session.GateTemplateScale ?? session.BaselineGateScale);
                        var singleGateConfidence = MapAlignmentConfidence
                            .ComputeSingleGateTrackingConfidence(
                                evidence.Score,
                                session.LastConfidence,
                                scaleAgreement);

                        singleGateProposal =
                            MapCvRecognitionBuilders.BuildTrackedRecognition(
                                fingerprint,
                                transform,
                                [evidence],
                                MapRecognitionSource.SingleGateTracking,
                                confidenceOverride: singleGateConfidence,
                                evidenceKind: MapAlignmentEvidenceKind.None);
                        structureSeed = transform;
                        freshAnchorTransform = transform;
                    }
                }
            }

            diagnostics.UsedSingleGateStructureFallback =
                singleGateProposal is null;
            diagnostics.SingleGateFallbackReason =
                singleGateFallbackReason ?? string.Empty;
        }

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

        MapAuxiliaryTrackingResult? auxiliary = null;
        if (structureTuning.UseAuxiliaryAnchorRecognition)
        {
            auxiliary = MapAnchorTracker.TrackAuxiliaryAnchors(
                reference,
                frame.Image,
                fingerprint,
                frame.ViewportBounds,
                session.LockedTransform,
                tuning.GateTemplateThreshold,
                tuning.ConfirmationAdvantage,
                structureTuning.MaximumAuxiliaryTemplates,
                service.AuxiliaryTemplateCache);
            diagnostics.AuxiliaryAnchorMilliseconds =
                auxiliary.SearchMilliseconds;
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"辅助锚点追踪{(auxiliary.IsSuccess ? "成功" : "失败")} · "
                + $"{auxiliary.Matches.Count} 个匹配 · "
                + $"{auxiliary.SearchMilliseconds:F0}ms",
                elapsedMs: auxiliary.SearchMilliseconds);
            diagnostics.AuxiliaryAnchorMatchCount = auxiliary.Matches.Count;
            diagnostics.AuxiliaryTemplatesEvaluated =
                auxiliary.TemplatesEvaluated;
            diagnostics.AuxiliaryUsedGlobalSearch =
                auxiliary.UsedGlobalSearch;
            diagnostics.AuxiliaryConfidence = auxiliary.Confidence;
            dynamicIgnoreRegions.AddRange(
                auxiliary.Matches
                    .Select(match => MapCvRecognitionBuilders.ToLocalRect(
                        match.ScreenBounds,
                        frame.ViewportBounds,
                        frame.Image.Size()))
                    .Where(region =>
                        region.Width > 0 && region.Height > 0));
            if (auxiliary.IsSuccess
                && MapOverlayTransformSolver.TryTranslateWithLockedScale(
                    session.LockedTransform,
                    auxiliary.Matches,
                    out var proposedSeed,
                    out _))
            {
                structureSeed = proposedSeed;
                freshAnchorTransform = proposedSeed;
            }

            if (MapCvRecognitionBuilders.TryBuildDirectAuxiliaryRecognition(
                    fingerprint,
                    session,
                    singleGateProposal,
                    auxiliary,
                    frame.ViewportBounds,
                    structureTuning.AuxiliaryDirectLockConfidence,
                    out var auxiliaryRecognition))
            {
                diagnostics.TrackingMode =
                    MapAlignmentTrackingMode.AuxiliaryAnchorTracking;
                diagnostics.AlignmentEvidence =
                    auxiliaryRecognition!.Result.EvidenceKind;
                diagnostics.SkippedStructureValidation = true;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    Recognition = auxiliaryRecognition,
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                };
            }
        }

        dynamicIgnoreRegions.AddRange(
            MapCvRecognitionBuilders.BuildProjectedOutsideIgnoreRegions(
                fingerprint.Map,
                fingerprint.FloorKey,
                frame,
                structureSeed));

        var primaryProfile = MapFloorRules.GetFloorProfile(
            fingerprint.Map,
            fingerprint.FloorKey)
            ?? fingerprint.Map.Recognition.FirstFloor;

        stopwatch.Restart();
        using var preparedReference = service.StructureCache.GetOrCreate(
            fingerprint.Map.Id,
            fingerprint.Map.UpdatedAt,
            reference,
            primaryProfile.WholeImageIgnoreRegions,
            fingerprint.FloorKey);
        stopwatch.Stop();
        diagnostics.CacheMilliseconds += stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        using var preparedLive = service.StructurePreprocessor.ProcessLiveRoi(
            frame.Image,
            liveIgnoreRegions,
            dynamicIgnoreRegions);
        stopwatch.Stop();
        diagnostics.StructurePreprocessMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;

        var hasAnchorSeed = singleGateProposal is not null
            || auxiliary?.IsSuccess is true;
        // A side-entrance scan seed already contains a same-frame feature
        // match, scale and translation.  Requiring the generic single-gate
        // classifier to identify that gate again discards the strongest scan
        // evidence on sparse screenshots and restarts structure registration
        // at scale 1.0.  Keep the scan seed as the restricted, scale-searching
        // structure prior; the registrar still has to accept independent wall
        // structure before the map is committed.
        var isSideEntranceStructureRoute = route == SelectedAlignmentRoute.SideEntrance
            && (singleGateProposal is not null
                || searchCtx?.UseRestrictedStructureFallback == true);
        if (route == SelectedAlignmentRoute.SideEntrance
            && singleGateProposal is null)
        {
            var fallbackKind = gates.Count switch
            {
                0 => "未检测到门",
                1 when !string.IsNullOrWhiteSpace(singleGateFallbackReason)
                    => "单门身份确认失败",
                1 => "单门未形成可靠侧门证据",
                _ => "检测到多扇门"
            };
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"侧门单门复核不可用，保留扫描种子进行结构验证 · {fallbackKind}",
                details: new()
                {
                    ["gateCount"] = gates.Count,
                    ["fallbackKind"] = fallbackKind,
                    ["singleGateFallbackReason"] = singleGateFallbackReason ?? string.Empty,
                    ["allowScaleSearch"] = isSideEntranceStructureRoute,
                    ["restrictSearchToLockedTransform"] = isSideEntranceStructureRoute
                });
        }
        var structureSearchTuning = structureTuning.Clone();
        var hasReliableCurrentSideSeed =
            searchCtx?.UseRestrictedStructureFallback == true
            && session.SideEntranceScanPriorConfidence >= 0.80d;
        if (hasReliableCurrentSideSeed)
        {
            // The side-feature match already selected one location basin in
            // this exact frame. Nearby corridor candidates are therefore not
            // independent map choices and must not reintroduce ambiguity.
            structureSearchTuning.MinimumCandidateMargin = 0d;
        }
        if (!isSideEntranceStructureRoute)
        {
            structureSearchTuning.TopCandidateCount = Math.Min(
                3,
                structureSearchTuning.TopCandidateCount);
        }

        var restrictStructureSearch = isSideEntranceStructureRoute || hasAnchorSeed;
        var structureRequest = new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = frame.Image,
            ViewportBounds = frame.ViewportBounds,
            LockedTransform = structureSeed,
            Tuning = structureSearchTuning,
            ScaleSearchPolicy = isSideEntranceStructureRoute
                ? MapScaleSearchPolicy.Search
                : MapScaleSearchPolicy.Fixed,
            RestrictSearchToLockedTransform = restrictStructureSearch,
            TrackingMode = true,
            ForceBestCandidate = false,
            PreparedReference = preparedReference,
            PreparedLive = preparedLive,
            FixedRotationDegrees = primaryProfile.OrientationDegrees,
            ValidMapBounds = primaryProfile.GetEffectiveValidMapBounds(),
            PlayerPrior = playerPrior,
            PredictedViewportOrigin = predictedViewportOrigin,
            LiveIgnoreRegions = liveIgnoreRegions ?? [],
            DynamicIgnoreRegions = dynamicIgnoreRegions,
            CandidateHistory = candidateHistory ?? [],
            SideEntrancePrior = 0d
        };
        var structure = service.StructureRegistrar.Register(structureRequest);
        if (isSideEntranceStructureRoute
            && restrictStructureSearch
            && !structure.Accepted)
        {
            var globalRecoveryRequest = new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = structureSeed,
                Tuning = structureSearchTuning,
                ScaleSearchPolicy = MapScaleSearchPolicy.Search,
                RestrictSearchToLockedTransform = false,
                TrackingMode =
                    MapAlignmentSearchPolicy.UseTrackingForGlobalRecovery(
                        searchCtx),
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                FixedRotationDegrees = primaryProfile.OrientationDegrees,
                ValidMapBounds = primaryProfile.GetEffectiveValidMapBounds(),
                PlayerPrior = playerPrior,
                PredictedViewportOrigin = predictedViewportOrigin,
                LiveIgnoreRegions = liveIgnoreRegions ?? [],
                DynamicIgnoreRegions = dynamicIgnoreRegions,
                CandidateHistory = candidateHistory ?? [],
                SideEntrancePrior = 0d
            };
            var globalRecovery = service.StructureRegistrar.Register(
                globalRecoveryRequest);
            if (globalRecovery.Accepted
                || (!structure.Accepted
                    && (globalRecovery.Confidence > structure.Confidence
                        || globalRecovery.BestScore < structure.BestScore)))
            {
                MapLogCollector.Instance.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    "侧门结构局部搜索未通过，已尝试全局恢复",
                    details: new()
                    {
                        ["localAccepted"] = structure.Accepted,
                        ["localBestScore"] = structure.BestScore,
                        ["globalAccepted"] = globalRecovery.Accepted,
                        ["globalBestScore"] = globalRecovery.BestScore,
                        ["globalConfidence"] = globalRecovery.Confidence
                    });
                structure = globalRecovery;
            }
        }

        MapCvRecognitionDiagnostics.WriteStructureDebugResult(
            fingerprint.Map,
            structure,
            singleGateFallbackReason);

        diagnostics.StructureSearchMilliseconds =
            structure.SearchMilliseconds;
        diagnostics.StructureRefineMilliseconds =
            structure.RefineMilliseconds;
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence =
            MapAlignmentEvidenceKind.Structure;
        PopulateStructureDiagnostics(diagnostics, structure);

        var effectiveStructureConfidence = structure.Confidence;

        var postStructureTimer = Stopwatch.StartNew();
        if (!structure.Accepted
            || structure.Transform is null
            || (effectiveStructureConfidence < tuning.MinimumConfidence
                && !tuning.ForceBestRecognitionResult))
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            if (tuning.ForceBestRecognitionResult)
            {
                diagnostics.UsedForcedBestResult = true;
                diagnostics.StructureAttempted = true;
                diagnostics.StructureAccepted = false;
                diagnostics.StructureFailureReason =
                    structure.FailureReason;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    StructureResult = structure,
                    Recognition = MapCvRecognitionBuilders
                        .BuildReusedTransformRecognition(
                            fingerprint,
                            session,
                            structure),
                    GateDetectionResult = gateResult,
                    SearchStage = diagnostics.SearchStage,
                    StructureAttempted = true,
                    StructureAccepted = false,
                    StructureFailureReason = structure.FailureReason,
                };
            }

            var failureReason = structure.Accepted
                && structure.Confidence < tuning.MinimumConfidence
                    ? $"结构配准置信度 {structure.Confidence:P0} 低于阈值 {tuning.MinimumConfidence:P0}"
                    : structure.FailureReason;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = failureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason =
                    (singleGateFallbackReason is null
                        ? string.Empty
                        : $"{singleGateFallbackReason}；已回退结构配准，但")
                    + $"{failureReason}；已保留最后可靠对齐，等待下次开图恢复。",
                GateDetectionResult = gateResult,
                SearchStage = diagnostics.SearchStage,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = failureReason,
            };
        }

        var gateBaseline = session.BaselineGateScale > 0d
            ? session.BaselineGateScale
            : session.LockedTransform.ScaleX;
        if (Math.Abs((structure.Transform.ScaleX / gateBaseline) - 1d)
                > structureTuning.ScaleSearchRadius + 0.0001d
            && !tuning.ForceBestRecognitionResult)
        {
            var rejected = MapStructureRegistrationResult.Reject(
                MapStructureRejectionReason.ScaleChangeTooLarge,
                candidates: structure.Candidates,
                preprocessMilliseconds: structure.PreprocessMilliseconds,
                searchMilliseconds: structure.SearchMilliseconds,
                debugOutputDirectory: structure.DebugOutputDirectory);
            return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                diagnostics,
                rejected,
                $"{rejected.FailureReason}；已保留最后可靠对齐，等待双门重新锁定。",
                gateResult,
                diagnostics.SearchStage);
        }

        if (isSideEntranceStructureRoute && freshAnchorTransform is not null)
        {
            var maxDeviation = Math.Max(
                Math.Abs(
                    structure.Transform.OffsetX
                    - freshAnchorTransform.OffsetX),
                Math.Abs(
                    structure.Transform.OffsetY
                    - freshAnchorTransform.OffsetY));
            if (maxDeviation > MapCvRecognitionService.SideEntranceAnchorDeviationTolerancePixels)
            {
                var rejected = MapStructureRegistrationResult.Reject(
                    MapStructureRejectionReason.AnchorTransformConflict,
                    candidates: structure.Candidates,
                    preprocessMilliseconds: structure.PreprocessMilliseconds,
                    searchMilliseconds: structure.SearchMilliseconds,
                    debugOutputDirectory: structure.DebugOutputDirectory);
                return MapCvRecognitionBuilders.BuildStructureRejectedAttempt(
                    diagnostics,
                    rejected,
                    $"{rejected.FailureReason}；结构结果与本次锚点位置偏差超过 "
                    + $"{MapCvRecognitionService.SideEntranceAnchorDeviationTolerancePixels:F0}px，已拒绝。",
                    gateResult,
                    diagnostics.SearchStage);
            }
        }

        diagnostics.TrackingMode = isSideEntranceStructureRoute
            ? MapAlignmentTrackingMode.StructureMatched
            : singleGateProposal is null
                ? MapAlignmentTrackingMode.StructureMatched
                : MapAlignmentTrackingMode.SingleGateTracking;
        diagnostics.UsedForcedBestResult =
            tuning.ForceBestRecognitionResult
            && (structure.WasForcedBestCandidate
                || structure.Confidence < tuning.MinimumConfidence);
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = structure.Accepted;
        diagnostics.StructureFailureReason =
            structure.Accepted ? string.Empty : structure.FailureReason;

        postStructureTimer.Stop();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构后处理完成 · {postStructureTimer.Elapsed.TotalMilliseconds:F0}ms",
            elapsedMs: postStructureTimer.Elapsed.TotalMilliseconds);
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = MapCvRecognitionBuilders.BuildStructureRecognition(
                fingerprint,
                structure.Transform,
                structure,
                diagnostics.UsedForcedBestResult,
                singleGateProposal,
                confidenceOverride: session.SideEntranceScanPriorConfidence >= 0.80d
                    ? Math.Max(
                        session.SideEntranceScanPriorConfidence,
                        structure.Confidence)
                    : null),
            GateDetectionResult = gateResult,
            SearchStage = diagnostics.SearchStage,
            StructureAttempted = true,
            StructureAccepted = structure.Accepted,
            StructureFailureReason =
                structure.Accepted ? string.Empty : structure.FailureReason,
        };
    }

}
