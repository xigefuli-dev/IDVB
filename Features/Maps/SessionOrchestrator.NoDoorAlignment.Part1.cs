using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

    private MapRecognitionAttempt AlignNoDoorLocalStructure(
        CapturedGameFrame frame,
        RuntimeMapRecognition locked,
        string floorKey,
        MapAlignmentSession sameFloorSession,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        IReadOnlyList<MapSimilarityTransform> candidateHistory,
        double identityPriorConfidence,
        bool allowTrackingScaleSearch = false)
    {
        structureTuning = CreateStructureTuningForFloor(
            locked.Map,
            floorKey,
            structureTuning);
        if (!TryCreateNoDoorStageTuning(
                structureTuning,
                out var localTuning,
                maximumStageMilliseconds: 500))
        {
            return CreateNoDoorBudgetFailure("same-floor-local");
        }

        var totalTimer = Stopwatch.StartNew();
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            _recognition.ReadyMapCount,
            _recognition.TotalMapCount);
        if (alignmentMode != MapOverlayAlignmentMode.Uniform)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "无门楼层局部跟踪只支持等比缩放。");
        }

        var profile = MapFloorRules.GetFloorProfile(locked.Map, floorKey);
        if (profile is null)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"地图中不存在楼层 '{floorKey}'。");
        }

        var cacheTimer = Stopwatch.StartNew();
        var referenceProfile = MapCvRecognitionService.GetReferenceProfile(
            structureTuning,
            MapStructurePreprocessingProfile.EdgesOnly);
        var residentLease = _recognition.StructureCache.TryRentResident(
            locked.Map.Id,
            locked.Map.UpdatedAt,
            floorKey,
            structureTuning.Generation,
            referenceProfile);
        cacheTimer.Stop();
        diagnostics.ReferenceCacheMilliseconds = cacheTimer.Elapsed.TotalMilliseconds;
        MapStructureFeatures? ownedPreparedReference = null;
        Mat? decodedReference = null;
        if (residentLease is null)
        {
            var referencePath = _recognition.GetAlignmentReferencePath(
                locked.Map, floorKey, structureTuning);
            var referenceTimer = Stopwatch.StartNew();
            decodedReference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
            referenceTimer.Stop();
            diagnostics.ReferenceImageLoadMilliseconds =
                referenceTimer.Elapsed.TotalMilliseconds;
            diagnostics.ReferenceDiskReadCount = 1;
            if (decodedReference.Empty())
            {
                decodedReference.Dispose();
                residentLease?.Dispose();
                return MapCvRecognitionDiagnostics.Failure(
                    diagnostics,
                    $"无法读取楼层 '{floorKey}' 的识别图。");
            }

            cacheTimer.Restart();
            ownedPreparedReference = _recognition.StructureCache.GetOrCreate(
                locked.Map.Id,
                locked.Map.UpdatedAt,
                decodedReference,
                profile.WholeImageIgnoreRegions,
                floorKey,
                structureTuning.Generation,
                referenceProfile);
            cacheTimer.Stop();
            diagnostics.ReferenceCacheMilliseconds += cacheTimer.Elapsed.TotalMilliseconds;
        }
        using var ownedDecodedReference = decodedReference;
        using var leaseScope = residentLease;
        using var ownedPreparedReferenceScope = ownedPreparedReference;
        using var emptyReference = decodedReference is null ? new Mat() : null;
        var reference = decodedReference ?? emptyReference!;
        var preparedReference = residentLease?.Features ?? ownedPreparedReference!;
        MapStructureFeatures preparedLive;
        MapStructureFeatures? ownedPreparedLive = null;
        MapStructureFeatures? ownedPreparedOriginalLive = null;
        var liveCacheHit = false;
        double liveExtractionMilliseconds;
        if (structureTuning.UsePrebuiltStructureLine)
        {
            _recognition.CreatePrebuiltLiveStructureFeatures(
                frame,
                out ownedPreparedLive,
                out ownedPreparedOriginalLive,
                out liveExtractionMilliseconds);
            preparedLive = ownedPreparedLive;
        }
        else
        {
            preparedLive = frame.GetOrCreateDefaultLiveStructureFeatures(
                _recognition.StructurePreprocessor,
                MapStructurePreprocessingProfile.EdgesOnly,
                out liveCacheHit,
                out liveExtractionMilliseconds,
                out _,
                generateVisibleMask: structureTuning.EnableVisibleMask,
                generationTuning: structureTuning.Generation);
        }
        using var ownedPreparedLiveScope = ownedPreparedLive;
        using var ownedPreparedOriginalLiveScope = ownedPreparedOriginalLive;
        diagnostics.StructurePreprocessMilliseconds = liveCacheHit
            ? 0d
            : liveExtractionMilliseconds;
        diagnostics.LiveStructurePreprocessMilliseconds =
            diagnostics.StructurePreprocessMilliseconds;
        diagnostics.StructurePreprocessCount = liveCacheHit ? 0 : 1;
        if (NoDoorAlignmentDeadline.Current?.IsExpired == true)
            return CreateNoDoorBudgetFailure("same-floor-local-preprocess", diagnostics);

        if (!TryCreateNoDoorStageTuning(
                localTuning,
                out var postPreprocessTuning,
                maximumStageMilliseconds: 500))
        {
            return CreateNoDoorBudgetFailure(
                "same-floor-local-preprocess",
                diagnostics);
        }
        localTuning = postPreprocessTuning;

        localTuning.ScaleSearchRadius = 0d;
        if (!allowTrackingScaleSearch)
            localTuning.TrackingScaleSearchRadius = 0d;
        localTuning.TrackingSearchRadiusPixels =
            localTuning.PreviousAlignmentSearchRadiusPixels;
        localTuning.EnableFeatureVoting = false;
        // The acceptance window is a diagnostic target for this route. Do not
        // terminate the locked transform evaluation just because a warm path
        // crossed the target while the result is still being validated.
        localTuning.EnforceTimeBudget = false;
        // The first Steady attempt is deliberately local. A miss is not a
        // terminal result: below we keep the exact same floor scale and
        // current frame, then expand translation search without borrowing
        // another floor's seed.
        localTuning.EnableFastAlignment = true;
        localTuning.FastFallbackToLegacy = false;
        localTuning.FastCoarseDownsampleFactor = 4;
        localTuning.FastCoarseTopK = 3;
        localTuning.EnableVisibleMask = true;
        localTuning.VisibleAwareCorrelationMode =
            VisibleAwareCorrelationMode.CoarseMat;
        localTuning.VisibleAwareCoarseDownsample = 4;
        localTuning.VisibleAwareTopK = 3;
        localTuning.EnableVisibleAwareShadow = false;
        localTuning.EnableVisibleAwareInjection = true;
        diagnostics.GateDetectionAttempted = false;
        diagnostics.VpsgAttempted = false;
        diagnostics.UmatAttempted = false;
        diagnostics.FullResolutionTemplateMatchCount = 0;
        MapStructureRegistrationRequest CreateRequest(
            MapStructureRegistrationTuning requestTuning,
            bool restrictTranslation,
            bool trackingMode) =>
            new()
            {
                ReferenceImage = reference,
                Channel = requestTuning.Channel,
                LiveRoi = frame.Image,
                ViewportBounds = frame.ViewportBounds,
                LockedTransform = sameFloorSession.LockedTransform,
                Tuning = requestTuning,
                ScaleSearchPolicy = allowTrackingScaleSearch
                    ? MapScaleSearchPolicy.Search
                    : MapScaleSearchPolicy.Fixed,
                RestrictSearchToLockedTransform = restrictTranslation,
                TrackingMode = trackingMode,
                ForceBestCandidate = false,
                PreparedReference = preparedReference,
                PreparedLive = preparedLive,
                PreparedOriginalLive = ownedPreparedOriginalLive,
                FixedRotationDegrees = profile.OrientationDegrees,
                ValidMapBounds = profile.GetEffectiveValidMapBounds(),
                CandidateHistory = candidateHistory,
                SideEntrancePrior = 0d
            };

        var localStructure = _recognition.StructureRegistrar.Register(
            CreateRequest(localTuning, restrictTranslation: true, trackingMode: true));
        var structure = localStructure;
        var usedGlobalTranslationRecovery = false;
        if (!allowTrackingScaleSearch
            && !MapOpenAlignmentRouteRules.IsAcceptedStructureAlignment(
                structureTuning.Channel,
                localStructure.Accepted,
                localStructure.Transform is not null,
                localStructure.Confidence,
                tuning.MinimumConfidence))
        {
            // Local evidence can leave the 96px tracking window as the game
            // recenters its large map. Continue on the same observation with
            // fixed scale and unrestricted translation. Fast global peaks are
            // tried first; only an exceptional fast miss reaches the complete
            // fixed-scale legacy search. No elapsed-time target truncates it.
            var recoveryTuning = localTuning.Clone();
            MapOpenAlignmentRouteRules
                .ApplySteadyGlobalTranslationRecoveryPolicy(recoveryTuning);
            _logCollector.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"Steady 局部验证未通过，继续固定尺度全局平移恢复 · floor={floorKey}",
                details: new()
                {
                    ["floor"] = floorKey,
                    ["scale"] = sameFloorSession.LockedTransform.ScaleX,
                    ["localRejection"] = localStructure.RejectionReason.ToString(),
                    ["localConfidence"] = localStructure.Confidence,
                    ["localCandidateMargin"] = localStructure.CandidateMargin,
                    ["localSearchMs"] = localStructure.SearchMilliseconds,
                    ["candidateHistoryCount"] = candidateHistory.Count,
                    ["scaleSearchPolicy"] = nameof(MapScaleSearchPolicy.Fixed),
                    ["restrictTranslation"] = false
                });
            structure = _recognition.StructureRegistrar.Register(
                CreateRequest(
                    recoveryTuning,
                    restrictTranslation: false,
                    trackingMode: false));
            usedGlobalTranslationRecovery = true;
            if (structure.Accepted && structure.Transform is not null)
            {
                var lockedTransform = sameFloorSession.LockedTransform;
                var dx = structure.Transform.OffsetX - lockedTransform.OffsetX;
                var dy = structure.Transform.OffsetY - lockedTransform.OffsetY;
                var driftDistance = Math.Sqrt((dx * dx) + (dy * dy));
                _logCollector.Append(
                    MapLogCategory.StructureRegistration,
                    MapLogLevel.Info,
                    $"Steady 全局平移恢复已接受 · drift={driftDistance:F1}px · floor={floorKey}",
                    details: new()
                    {
                        ["driftDistance"] = driftDistance,
                        ["recoveredOffsetX"] = structure.Transform.OffsetX,
                        ["recoveredOffsetY"] = structure.Transform.OffsetY,
                        ["lockedOffsetX"] = lockedTransform.OffsetX,
                        ["lockedOffsetY"] = lockedTransform.OffsetY
                    });
            }
        }
        totalTimer.Stop();
        MapCvAlignmentService.PopulateStructureDiagnostics(
            diagnostics,
            structure);
        diagnostics.StructureSearchMilliseconds =
            localStructure.SearchMilliseconds
            + (usedGlobalTranslationRecovery
                ? structure.SearchMilliseconds
                : 0d);
        diagnostics.StructureRefineMilliseconds =
            localStructure.RefineMilliseconds
            + (usedGlobalTranslationRecovery
                ? structure.RefineMilliseconds
                : 0d);
        diagnostics.StructureBestScore = structure.BestScore;
        diagnostics.StructureSecondScore = structure.SecondScore;
        diagnostics.StructureCandidateMargin = structure.CandidateMargin;
        diagnostics.StructureRejectionReason = structure.RejectionReason;
        diagnostics.StructureDisposition =
            structure.RejectionReason.ToDisposition(structure.Accepted);
        diagnostics.AlignmentEvidence = MapAlignmentEvidenceKind.Structure;
        diagnostics.FullResolutionTemplateMatchCount =
            usedGlobalTranslationRecovery && !structure.UsedFastStrategy
                ? 1
                : 0;
        diagnostics.TotalMilliseconds = totalTimer.Elapsed.TotalMilliseconds;
        if (NoDoorAlignmentDeadline.Current?.IsExpired == true)
            return CreateNoDoorBudgetFailure("same-floor-local", diagnostics);

        if (!MapOpenAlignmentRouteRules.IsAcceptedStructureAlignment(
                structureTuning.Channel,
                structure.Accepted,
                structure.Transform is not null,
                structure.Confidence,
                tuning.MinimumConfidence))
        {
            diagnostics.TrackingMode =
                MapAlignmentTrackingMode.HoldingLastTransform;
            return new MapRecognitionAttempt
            {
                Diagnostics = diagnostics,
                StructureResult = structure,
                FailureReason = structure.FailureReason,
                StructureAttempted = true,
                StructureAccepted = false,
                StructureFailureReason = structure.FailureReason,
                SearchStage = AlignmentSearchStage.StructureFallback
            };
        }

        diagnostics.TrackingMode = MapAlignmentTrackingMode.StructureMatched;
        diagnostics.StructureAccepted = true;
        return new MapRecognitionAttempt
        {
            Diagnostics = diagnostics,
            StructureResult = structure,
            Recognition =
                MapCvRecognitionBuilders.BuildFloorStructureRecognition(
                    locked.Map,
                    floorKey,
                    _recognition.Repository.GetFloorOverlayPath(
                        locked.Map,
                        floorKey),
                    structure.Transform!,
                    structure,
                    identityPriorConfidence),
            StructureAttempted = true,
            StructureAccepted = true,
            SearchStage = AlignmentSearchStage.StructureFallback
        };
    }

    // 辅助锚点已停用（TryAlignNoDoorWithAuxiliaryAnchors 已移除）。
}
