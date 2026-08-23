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
        double identityPriorConfidence,
        bool restrictTranslationToSeed)
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

        // Compatibility placeholder. Routing policy decides whether this
        // structure-only API is appropriate; the registrar itself must not
        // turn a real structure rejection into a double-gate requirement.
        _ = allowPrimaryFloor;

        var profile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (profile is null)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"The selected map does not contain floor '{floorKey}'.");
        }

        var resolvedChannel = MapAlignmentChannelRegistry.Resolve(map, floorKey);
        if (structureTuning.Channel != resolvedChannel.Channel)
        {
            diagnostics.AlignmentChannel = structureTuning.Channel ==
                MapAlignmentChannel.LowStructure
                    ? MapAlignmentChannelRegistry.LowStructure.DiagnosticLabel
                    : MapAlignmentChannelRegistry.Standard.DiagnosticLabel;
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"Floor '{floorKey}' requires alignment channel "
                + $"'{resolvedChannel.DiagnosticLabel}', but received "
                + $"'{diagnostics.AlignmentChannel}'.");
        }
        diagnostics.AlignmentChannel = resolvedChannel.DiagnosticLabel;
        diagnostics.FloorMarkerKeys = string.Join(
            ",",
            MapFloorMarkerRules.Normalize(
                MapFloorRules.GetOrderedFloors(map)
                    .First(floor => string.Equals(
                        floor.Key,
                        floorKey,
                        StringComparison.Ordinal))
                    .MarkerKeys));
        diagnostics.AlignmentConfigFingerprint =
            resolvedChannel.Channel == MapAlignmentChannel.LowStructure
                ? structureTuning.CacheFingerprint
                : "legacy";

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

        // 结构缓存常驻命中时不需要参考图像素——Registrar 只在缺少
        // PreparedReference 时才拿它现场预处理。此前每次对齐都要解码一张上百万
        // 像素的识别图（实测均值 15ms），只为通过判空与缓存的尺寸校验。
        var cacheTimer = Stopwatch.StartNew();
        var residentLease = service.StructureCache.TryRentResident(
            map.Id,
            map.UpdatedAt,
            floorKey,
            structureTuning.Generation);
        cacheTimer.Stop();
        diagnostics.ReferenceCacheMilliseconds =
            cacheTimer.Elapsed.TotalMilliseconds;

        Mat? decodedReference = null;
        MapStructureFeatures? ownedPreparedReference = null;
        if (residentLease is null)
        {
            var referencePath = service.Repository.GetFloorRecognitionPath(
                map,
                floorKey);
            if (!File.Exists(referencePath))
            {
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"The recognition image for floor '{floorKey}' is missing.");
            }

            var referenceLoadTimer = Stopwatch.StartNew();
            decodedReference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
            referenceLoadTimer.Stop();
            diagnostics.ReferenceImageLoadMilliseconds =
                referenceLoadTimer.Elapsed.TotalMilliseconds;
            if (decodedReference.Empty())
            {
                decodedReference.Dispose();
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"The recognition image for floor '{floorKey}' cannot be read.");
            }

            cacheTimer.Restart();
            ownedPreparedReference = service.StructureCache.GetOrCreate(
                map.Id,
                map.UpdatedAt,
                decodedReference,
                profile.WholeImageIgnoreRegions,
                floorKey,
                structureTuning.Generation);
            cacheTimer.Stop();
            diagnostics.ReferenceCacheMilliseconds +=
                cacheTimer.Elapsed.TotalMilliseconds;
        }
        using var ownedDecodedReference = decodedReference;
        using var leaseScope = residentLease;
        using var ownedPreparedReferenceScope = ownedPreparedReference;
        // 常驻命中时这是缓存持有的共享实例（只读使用，不得释放）；未命中时是
        // GetOrCreate 交出的副本，由上面的 using 负责释放。
        var preparedReference = residentLease?.Features ?? ownedPreparedReference!;
        diagnostics.CacheMilliseconds = diagnostics.ReferenceCacheMilliseconds;

        IReadOnlyList<Rect> dynamicIgnoreRegions = useProjectedBoundaryMask
            ? MapCvRecognitionBuilders.BuildProjectedOutsideIgnoreRegions(
                map, floorKey, frame, scaleSeed)
            : [];

        var stopwatch = Stopwatch.StartNew();
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"结构参考输入就绪 · floor={floorKey}",
            elapsedMs: diagnostics.ReferenceImageLoadMilliseconds
                + diagnostics.ReferenceCacheMilliseconds,
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["referenceImageLoadMs"] =
                    diagnostics.ReferenceImageLoadMilliseconds,
                ["referenceCacheMs"] = diagnostics.ReferenceCacheMilliseconds,
                ["referenceDecoded"] = decodedReference is not null,
                ["referenceWidth"] = preparedReference.Edges.Width,
                ["referenceHeight"] = preparedReference.Edges.Height
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
                out liveTiming,
                generateVisibleMask: structureTuning.EnableVisibleMask,
                generationTuning: structureTuning.Generation);
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
                    profile: livePreprocessingProfile,
                    generateVisibleMask: structureTuning.EnableVisibleMask,
                    generationTuning: structureTuning.Generation);
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

        if (structureTuning.Channel != MapAlignmentChannel.LowStructure
            && MapNoDoorAlignmentBudgetContext.RemainingMilliseconds
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

        var lowStructureScaleEstimate = TryEstimateLowStructureContentScale(
            service,
            map,
            floorKey,
            preparedReference,
            preparedLive,
            structureTuning,
            scaleSearchPolicy,
            isTracking,
            diagnostics,
            ref scaleSeed);

        MapStructureRegistrationRequest CreateRequest(
            MapScaleSearchPolicy policy,
            bool restrictTranslation) =>
            new()
            {
                // 缓存常驻命中时没有解码过的参考图；PreparedReference 已经是
                // Registrar 需要的全部输入，ReferenceImage 保持默认空 Mat。
                ReferenceImage = decodedReference ?? new Mat(),
                Channel = structureTuning.Channel,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = scaleSeed,
                Tuning = structureTuning,
                ScaleSearchPolicy = policy,
                RestrictSearchToLockedTransform =
                    policy == MapScaleSearchPolicy.Fixed
                    && restrictTranslation,
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
            };

        // A content-derived VPSG scale receives one strict fixed-scale
        // validation. If it fails, retain it only as the first exact global
        // hypothesis; it is neither cached nor treated as locked evidence.
        var structure = lowStructureScaleEstimate is not null
            ? service.StructureRegistrar.Register(
                CreateRequest(MapScaleSearchPolicy.Fixed, false))
            : service.StructureRegistrar.Register(
                CreateRequest(scaleSearchPolicy, restrictTranslationToSeed));
        if (lowStructureScaleEstimate is not null && !structure.Accepted)
        {
            diagnostics.ScaleBootstrapValidated = false;
            structure = service.StructureRegistrar.Register(
                CreateRequest(MapScaleSearchPolicy.Search, false));
        }
        else if (lowStructureScaleEstimate is not null)
        {
            diagnostics.ScaleBootstrapValidated = true;
        }

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
                ["channel"] = diagnostics.AlignmentChannel,
                ["floorMarkerKeys"] = diagnostics.FloorMarkerKeys,
                ["configFingerprint"] =
                    diagnostics.AlignmentConfigFingerprint,
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

        if (!MapOpenAlignmentRouteRules.IsAcceptedStructureAlignment(
                structureTuning.Channel,
                structure.Accepted,
                structure.Transform is not null,
                structure.Confidence,
                tuning.MinimumConfidence))
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
                structure.Transform!,
                structure,
                identityPriorConfidence),
            SearchStage = AlignmentSearchStage.StructureFallback,
            StructureAttempted = true,
            StructureAccepted = true,
            StructureFailureReason = string.Empty,
        };
    }
}
/*
 * 文件职责：MapCvAlignmentService.AlignStructureOnly。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
