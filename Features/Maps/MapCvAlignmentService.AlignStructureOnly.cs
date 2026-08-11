using OpenCvSharp;
using System.Diagnostics;

namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    internal static MapRecognitionAttempt AlignStructureOnly(
        MapCvRecognitionService service,
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning? structureTuning,
        MapReferencePoint? playerPrior,
        MapViewportOrigin? predictedViewportOrigin,
        IReadOnlyList<NormalizedRectangle>? liveIgnoreRegions,
        IReadOnlyList<MapSimilarityTransform>? candidateHistory,
        bool isTracking,
        bool useProjectedBoundaryMask,
        bool allowPrimaryFloor,
        MapScaleSearchPolicy scaleSearchPolicy,
        double identityPriorConfidence)
    {
        ObjectDisposedException.ThrowIf(service.IsDisposed, service);

        tuning = MapCvRecognitionHelpers.NormalizedCopy(tuning);
        tuning.ForceBestRecognitionResult = false;
        alignmentMode = MapOverlayAlignmentMode.Uniform;
        structureTuning ??= new MapStructureRegistrationTuning();
        structureTuning = structureTuning.Clone();
        structureTuning.Normalize();
        var livePreprocessingProfile =
            ResolveLiveStructurePreprocessingProfile(
                scaleSearchPolicy,
                isTracking,
                structureTuning);
        if (livePreprocessingProfile
            == MapStructurePreprocessingProfile.EdgesOnly)
        {
            // Edge-only inputs intentionally cannot contribute descriptor
            // votes. Avoid entering the feature-voting branch at all.
            structureTuning.EnableFeatureVoting = false;
        }
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            service.ReadyMapCount,
            service.TotalMapCount);
        var totalTimer = Stopwatch.StartNew();

        var map = service.TryGetMap(selectedMapId);
        if (map is null)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "当前选择的地图不存在或未加载。");
        }

        if (!allowPrimaryFloor
            && MapFloorRules.UsesDoubleGateAlignment(map, floorKey))
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The primary floor must use double-gate alignment.");
        }

        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (profile is null)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"The selected map does not contain floor '{floorKey}'.");
        }

        if (!double.IsFinite(scaleSeed.ScaleX)
            || scaleSeed.ScaleX <= 0.05d)
        {
            diagnostics.TrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"Floor '{floorKey}' has no valid primary scale seed.");
        }

        if (profile.OrientationDegrees != 0)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"Floor '{floorKey}' structure alignment requires 0-degree orientation.");
        }

        var referencePath = service.Repository.GetFloorRecognitionPath(map, floorKey);
        if (!File.Exists(referencePath))
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"The recognition image for floor '{floorKey}' is missing.");
        }

        var referenceLoadTimer = Stopwatch.StartNew();
        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
        referenceLoadTimer.Stop();
        diagnostics.ReferenceImageLoadMilliseconds =
            referenceLoadTimer.Elapsed.TotalMilliseconds;
        if (reference.Empty())
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"The recognition image for floor '{floorKey}' cannot be read.");
        }

        IReadOnlyList<Rect> dynamicIgnoreRegions = useProjectedBoundaryMask
            ? MapCvRecognitionBuilders.BuildProjectedOutsideIgnoreRegions(
                map, floorKey, frame, scaleSeed)
            : [];

        var stopwatch = Stopwatch.StartNew();
        using var preparedReference = service.StructureCache.GetOrCreate(
            map.Id,
            map.UpdatedAt,
            reference,
            profile.WholeImageIgnoreRegions,
            floorKey);
        stopwatch.Stop();
        diagnostics.CacheMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        diagnostics.ReferenceCacheMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构参考输入就绪 · floor={floorKey}",
            elapsedMs: referenceLoadTimer.Elapsed.TotalMilliseconds
                + stopwatch.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["referenceImageLoadMs"] =
                    referenceLoadTimer.Elapsed.TotalMilliseconds,
                ["referenceCacheMs"] = stopwatch.Elapsed.TotalMilliseconds,
                ["referenceWidth"] = reference.Width,
                ["referenceHeight"] = reference.Height
            });

        MapStructureFeatures preparedLive;
        MapStructureFeatures? ownedPreparedLive = null;
        PreprocessTiming liveTiming;
        bool liveFrameCacheHit;
        double originalExtractionMilliseconds;
        var canUseFrameCache = (liveIgnoreRegions is null
                || liveIgnoreRegions.Count == 0)
            && dynamicIgnoreRegions.Count == 0;
        if (canUseFrameCache)
        {
            preparedLive = frame.GetOrCreateDefaultLiveStructureFeatures(
                service.StructurePreprocessor,
                livePreprocessingProfile,
                out liveFrameCacheHit,
                out originalExtractionMilliseconds,
                out liveTiming);
        }
        else
        {
            stopwatch.Restart();
            ownedPreparedLive =
                service.StructurePreprocessor.ProcessLiveRoiDiagnostic(
                    frame.Image,
                    liveIgnoreRegions,
                    dynamicIgnoreRegions,
                    out liveTiming,
                    profile: livePreprocessingProfile);
            stopwatch.Stop();
            preparedLive = ownedPreparedLive;
            liveFrameCacheHit = false;
            originalExtractionMilliseconds =
                stopwatch.Elapsed.TotalMilliseconds;
        }
        using var ownedPreparedLiveDispose = ownedPreparedLive;
        var currentExtractionMilliseconds = liveFrameCacheHit
            ? 0d
            : originalExtractionMilliseconds;
        diagnostics.StructurePreprocessMilliseconds =
            currentExtractionMilliseconds;
        diagnostics.LiveStructurePreprocessMilliseconds =
            currentExtractionMilliseconds;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            liveFrameCacheHit
                ? "同一捕获帧的实时结构特征已复用"
                : "实时帧结构特征提取完成",
            elapsedMs: currentExtractionMilliseconds,
            details: CreateLiveStructureLogDetails(
                frame,
                preparedLive,
                liveTiming,
                liveFrameCacheHit
                    ? "captured-frame-cache"
                    : "new-extraction",
                originalExtractionMilliseconds,
                currentExtractionMilliseconds,
                diagnostics.ReferenceImageLoadMilliseconds,
                diagnostics.ReferenceCacheMilliseconds,
                liveIgnoreRegions?.Count ?? 0,
                dynamicIgnoreRegions.Count,
                requestedProfile: livePreprocessingProfile));

        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
                is { } remainingMilliseconds)
        {
            if (remainingMilliseconds
                < MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds)
            {
                const string reason =
                    "无门对齐预处理完成后已无足够的结构搜索预算，请保持地图打开并重试。";
                var timedOut = MapStructureRegistrationResult.Reject(
                    MapStructureRejectionReason.TimeBudgetExceeded,
                    reason);
                diagnostics.StructureAttempted = true;
                diagnostics.StructureAccepted = false;
                diagnostics.StructureRejectionReason =
                    MapStructureRejectionReason.TimeBudgetExceeded;
                diagnostics.StructureDisposition =
                    MapStructureEvidenceDisposition.Inconclusive;
                totalTimer.Stop();
                diagnostics.TotalMilliseconds =
                    totalTimer.Elapsed.TotalMilliseconds;
                return new MapRecognitionAttempt
                {
                    Diagnostics = diagnostics,
                    StructureResult = timedOut,
                    FailureReason = reason,
                    StructureAttempted = true,
                    StructureAccepted = false,
                    StructureFailureReason = reason,
                    SearchStage = AlignmentSearchStage.StructureFallback
                };
            }

            structureTuning.StructureFallbackBudgetMilliseconds = Math.Min(
                structureTuning.StructureFallbackBudgetMilliseconds,
                remainingMilliseconds);
        }

        var structure = service.StructureRegistrar.Register(
            new MapStructureRegistrationRequest
            {
                ReferenceImage = reference,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = scaleSeed,
                Tuning = structureTuning,
                ScaleSearchPolicy = scaleSearchPolicy,
                RestrictSearchToLockedTransform = false,
                TrackingMode = isTracking,
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                FixedRotationDegrees = profile.OrientationDegrees,
                ValidMapBounds = profile.GetEffectiveValidMapBounds(),
                PlayerPrior = playerPrior,
                PredictedViewportOrigin = predictedViewportOrigin,
                LiveIgnoreRegions = liveIgnoreRegions ?? [],
                DynamicIgnoreRegions = dynamicIgnoreRegions,
                CandidateHistory = candidateHistory ?? [],
                SideEntrancePrior = 0d
            });

        MapCvRecognitionDiagnostics.WriteStructureDebugResult(
            map, structure, null);
        MapCvAlignmentService.PopulateStructureDiagnostics(diagnostics, structure);

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
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
        totalTimer.Stop();
        diagnostics.TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            structure.Accepted ? MapLogLevel.Info : MapLogLevel.Warning,
            $"单次结构对齐阶段完成 · floor={floorKey} · accepted={structure.Accepted}",
            elapsedMs: totalTimer.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["scaleSearchPolicy"] = scaleSearchPolicy.ToString(),
                ["scaleSeed"] = scaleSeed.ScaleX,
                ["referenceImageLoadMs"] =
                    diagnostics.ReferenceImageLoadMilliseconds,
                ["referenceCacheMs"] =
                    diagnostics.ReferenceCacheMilliseconds,
                ["liveStructureExtractionMs"] =
                    diagnostics.LiveStructurePreprocessMilliseconds,
                ["liveFrameCacheHit"] = liveFrameCacheHit,
                ["preprocessingProfile"] =
                    liveTiming.Profile.ToString(),
                ["descriptorExtractionSkipped"] =
                    liveTiming.DescriptorExtractionSkipped,
                ["structureSearchMs"] = structure.SearchMilliseconds,
                ["structureRefineMs"] = structure.RefineMilliseconds,
                ["referenceWidth"] = structure.ReferenceWidth,
                ["referenceHeight"] = structure.ReferenceHeight,
                ["queryEdgePixels"] = structure.QueryEdgePixels,
                ["queryBoundsX"] = structure.QueryBoundsX,
                ["queryBoundsY"] = structure.QueryBoundsY,
                ["queryBoundsWidth"] = structure.QueryBoundsWidth,
                ["queryBoundsHeight"] = structure.QueryBoundsHeight,
                ["scaleHypotheses"] = structure.ScaleHypothesisCount,
                ["oversizedHypotheses"] =
                    structure.OversizedHypothesisCount,
                ["rejection"] = structure.RejectionReason.ToString(),
                ["failureReason"] = structure.FailureReason
            });

        if (!structure.Accepted
            || structure.Transform is null
            || structure.Confidence < tuning.MinimumConfidence)
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            diagnostics.StructureAttempted = true;
            diagnostics.StructureAccepted = false;
            diagnostics.StructureFailureReason = structure.FailureReason;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason =
                    $"{structure.FailureReason}; floor '{floorKey}' alignment was not locked.",
                SearchStage = AlignmentSearchStage.StructureFallback,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = structure.FailureReason,
            };
        }

        diagnostics.TrackingMode =
            MapAlignmentTrackingMode.StructureMatched;
        diagnostics.StructureAttempted = true;
        diagnostics.StructureAccepted = true;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition = MapCvRecognitionBuilders.BuildFloorStructureRecognition(
                map,
                floorKey,
                service.Repository.GetFloorOverlayPath(map, floorKey),
                structure.Transform,
                structure,
                identityPriorConfidence),
            SearchStage = AlignmentSearchStage.StructureFallback,
            StructureAttempted = true,
            StructureAccepted = true,
            StructureFailureReason = string.Empty,
        };
    }
}
