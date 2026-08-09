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
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            service.ReadyMapCount,
            service.TotalMapCount);

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

        using var reference = Cv2.ImRead(referencePath, ImreadModes.Unchanged);
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

        stopwatch.Restart();
        using var preparedLive = service.StructurePreprocessor.ProcessLiveRoi(
            frame.Image,
            liveIgnoreRegions,
            dynamicIgnoreRegions);
        stopwatch.Stop();
        diagnostics.StructurePreprocessMilliseconds =
            stopwatch.Elapsed.TotalMilliseconds;

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
