using System.Diagnostics;
using IDVBuff.Core.Diagnostics;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private const int LockedFloorFeatureMinimumInliers = 15;
    private const double LockedFloorFeatureMaximumResidual = 3d;
    private const double LockedFloorFeatureClusterTolerance = 5d;
    private const double LockedFloorFeatureMaximumScaleChange = 0.18d;

    private sealed record LockedFloorFeatureVote(
        double Scale,
        double OffsetX,
        double OffsetY,
        int ReferenceIndex,
        int LiveIndex,
        double DescriptorDistance);

    private sealed record LockedFloorFeatureFit(
        double Scale,
        double OffsetX,
        double OffsetY,
        int InlierCount,
        double Residual,
        double ReferenceSpan,
        double LiveSpan,
        double AverageDescriptorDistance,
        double Confidence);

    /// <summary>Estimates locked-floor structure scale, then validates it.</summary>
    public MapRecognitionAttempt AlignLockedFloorFeature(
        CapturedGameFrame frame,
        Guid selectedMapId,
        string floorKey,
        MapOverlayTransform scaleSeed,
        MapOverlayAlignmentMode alignmentMode,
        MapRecognitionTuning tuning,
        MapStructureRegistrationTuning structureTuning,
        double identityPriorConfidence,
        bool includeSiftFallback = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(scaleSeed);
        var diagnostics = MapCvRecognitionDiagnostics.CreateDiagnostics(
            ReadyMapCount,
            TotalMapCount);
        var map = TryGetMap(selectedMapId);
        var profile = map is null
            ? null
            : MapFloorRules.GetFloorProfile(map, floorKey);
        if (map is null || profile is null)
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked map floor is no longer available.");
        }

        if (MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel
            == MapAlignmentChannel.LowStructure)
        {
            var lowStructureTuning = structureTuning.Clone();
            lowStructureTuning.Channel = MapAlignmentChannel.LowStructure;
            lowStructureTuning.EnableFeatureVoting = false;
            lowStructureTuning.LowStructureEnableFeatureScaleEstimate = false;
            lowStructureTuning.Normalize();
            return AlignFloorWithoutGates(
                frame,
                selectedMapId,
                floorKey,
                scaleSeed,
                alignmentMode,
                tuning,
                lowStructureTuning,
                identityPriorConfidence: identityPriorConfidence,
                allowPrimaryFloor: true);
        }

        var effectiveScaleSeed = scaleSeed.ScaleX > 0.05 && !MapFloorScaleSeedRules.IsNeutralIndependentSeed(scaleSeed)
            ? (double?)scaleSeed.ScaleX
            : null;
        if (TryAlignWithVpsg3(
                frame,
                map,
                floorKey,
                identityPriorConfidence,
                out var vpsg3Attempt,
                out var vpsg3Status,
                knownScaleSeed: effectiveScaleSeed))
        {
            return vpsg3Attempt;
        }

        var vpsgFallbackCause = vpsg3Status;
        if (structureTuning.Mode == MapStructureRegistrationMode.ScanVerification)
        {
            return vpsg3Attempt ?? MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                $"VPSG 3.0 快速对齐未接受：{vpsg3Status}");
        }

        var stopwatch = Stopwatch.StartNew();
        LockedFloorFeatureFit? fit = null;
        var usedVpsg = false;
        VpsgBootstrapResult? vpsgBootstrap = null;
        var liveFrameCacheHit = false;
        var liveStructureExtractionMilliseconds = 0d;
        var preparedReferenceWidth = 0;
        var preparedReferenceHeight = 0;
        string rejectionReason;
        try
        {
            var vpsgMode = GetVpsgMode(structureTuning);
            var preprocessingProfile = vpsgMode == VpsgScaleMode.Structure
                ? MapStructurePreprocessingProfile.EdgesOnly
                : MapStructurePreprocessingProfile.EdgesAndFeatures;
            var referenceProfile = GetReferenceProfile(
                structureTuning,
                preprocessingProfile);
            using var residentReferenceLease = _structureCache.TryRentResident(
                map.Id,
                map.UpdatedAt,
                floorKey,
                structureTuning.Generation,
                referenceProfile);
            MapStructureFeatures? ownedPreparedReference = null;
            Mat? decodedReference = null;
            var referenceLoadMilliseconds = 0d;
            if (residentReferenceLease is null)
            {
                var referencePath = GetAlignmentReferencePath(
                    map, floorKey, structureTuning);
                var referenceLoadTimer = Stopwatch.StartNew();
                decodedReference = Cv2.ImRead(
                    referencePath,
                    ImreadModes.Grayscale);
                referenceLoadTimer.Stop();
                referenceLoadMilliseconds =
                    referenceLoadTimer.Elapsed.TotalMilliseconds;
                if (decodedReference.Empty())
                {
                    decodedReference.Dispose();
                    return MapCvRecognitionDiagnostics.Failure(
                        diagnostics,
                        "The locked floor recognition image could not be read.");
                }

                ownedPreparedReference = _structureCache.GetOrCreate(
                    map.Id,
                    map.UpdatedAt,
                    decodedReference,
                    profile.WholeImageIgnoreRegions,
                    floorKey,
                    structureTuning.Generation,
                    referenceProfile);
            }
            using var decodedReferenceScope = decodedReference;
            using var ownedPreparedReferenceScope = ownedPreparedReference;
            var preparedReference = residentReferenceLease?.Features
                ?? ownedPreparedReference!;
            preparedReferenceWidth = preparedReference.Edges.Width;
            preparedReferenceHeight = preparedReference.Edges.Height;
            diagnostics.ReferenceImageLoadMilliseconds = referenceLoadMilliseconds;
            diagnostics.ReferenceDiskReadCount = decodedReference is null ? 0 : 1;
            var preparedLive = frame.GetOrCreateDefaultLiveStructureFeatures(
                _structurePreprocessor,
                preprocessingProfile,
                out liveFrameCacheHit,
                out var originalLiveStructureExtractionMilliseconds,
                out _,
                generateVisibleMask: structureTuning.EnableVisibleMask,
                generationTuning: structureTuning.Generation);
            liveStructureExtractionMilliseconds = liveFrameCacheHit
                ? 0d
                : originalLiveStructureExtractionMilliseconds;
            var physicalPixelsPerComputationPixel =
                Math.Max(0.0001d, frame.PhysicalPixelsPerComputationPixel);
            vpsgBootstrap = EstimateVpsgScales(
                map,
                floorKey,
                preparedReference,
                preparedLive,
                vpsgMode,
                structurePriorScale: scaleSeed.ScaleX
                    / physicalPixelsPerComputationPixel,
                legacyPriorScale: scaleSeed.ScaleX
                    / physicalPixelsPerComputationPixel);
            var vpsgFit = CreateVpsgFit(
                vpsgBootstrap,
                physicalPixelsPerComputationPixel);
            if (vpsgFit is { } estimateFit)
            {
                usedVpsg = true;
                fit = estimateFit;
                rejectionReason = string.Empty;
            }
            else if (includeSiftFallback)
            {
                // VPSG does not use the previous floor as a hard scale gate.
                // The legacy feature route remains as a compatibility fallback,
                // also without the old +/-18% prior rejection. Default off in
                // the pipeline: it is the most expensive stage (full-image
                // SIFT + O(n^2) clustering) and the pipeline already has a
                // structured fallback chain (global recovery).
                fit = TryFitLockedFloorFeature(
                    preparedReference.NormalizedGray,
                    frame.Image,
                    double.NaN,
                    out var siftRejection);
                rejectionReason = $"VPSG: {vpsgBootstrap.StructureRejection}; "
                    + $"Legacy AKAZE: {vpsgBootstrap.LegacyRejection}; "
                    + $"SIFT: {siftRejection}";
            }
            else
            {
                fit = null;
                rejectionReason = vpsgBootstrap.StructureRejection;
                if (vpsgMode == VpsgScaleMode.LegacyAkaze)
                    rejectionReason = vpsgBootstrap.LegacyRejection;
            }
        }
        catch (OpenCVException exception)
        {
            fit = null;
            rejectionReason = $"OpenCV error: {exception.Message}";
        }
        stopwatch.Stop();
        if (fit is null)
        {
            diagnostics.ScaleBootstrapAttempted = true;
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"锁定楼层特征对齐未采用 · map={map.SequenceNumber}#{floorKey} · {rejectionReason}",
                elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["priorScale"] = scaleSeed.ScaleX,
                    ["referenceWidth"] = preparedReferenceWidth,
                    ["referenceHeight"] = preparedReferenceHeight,
                    ["liveWidth"] = frame.Image.Width,
                    ["liveHeight"] = frame.Image.Height,
                    ["liveFrameCacheHit"] = liveFrameCacheHit,
                    ["liveStructureExtractionMs"] =
                        liveStructureExtractionMilliseconds,
                    ["rejection"] = rejectionReason
                });
            var failureStatus = IdvbStatus.ClientError(
                IdvbHttpCode.UnprocessableVisual,
                IdvbSubCode.StructureWeakAbsoluteScore,
                "LockedFloorFeatureUnreliable",
                "锁定楼层未能提取到可靠的几何特征",
                technicalDetail: $"rejection={rejectionReason}",
                stage: "LockedFloorFeature.Fit",
                cause: vpsgFallbackCause);
            MapLogCollector.Instance.AppendStatus(failureStatus);
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked floor did not produce reliable feature geometry.",
                failureStatus);
        }

        // Once a bounded scan VPSG stage has started and produced scale basins,
        // its formal validation owns the stage budget. The outer scan clock
        // still decides whether this stage starts and whether another map runs.
        using var scanValidationBudget = structureTuning.Mode
                == MapStructureRegistrationMode.ScanVerification
            ? MapNoDoorAlignmentBudgetContext.Enter(
                () => structureTuning.StructureFallbackBudgetMilliseconds)
            : null;
        var attempt = ValidateVpsgScaleCandidates(
            frame,
            selectedMapId,
            map,
            floorKey,
            alignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence,
            fit,
            vpsgBootstrap!,
            usedVpsg,
            liveFrameCacheHit,
            liveStructureExtractionMilliseconds,
            stopwatch.Elapsed.TotalMilliseconds);

        if (attempt.Recognition is null && vpsgFallbackCause is not null)
        {
            var fallbackFailureStatus = attempt.Status is { } curStatus
                ? curStatus with { Cause = vpsgFallbackCause }
                : IdvbStatus.ClientError(
                    IdvbHttpCode.UnprocessableVisual,
                    IdvbSubCode.StructureWeakAbsoluteScore,
                    "LockedFloorAlignmentFailed",
                    string.IsNullOrWhiteSpace(attempt.FailureReason) ? "锁定楼层结构配准未通过" : attempt.FailureReason,
                    stage: "LockedFloorFeature.ValidateCandidates",
                    cause: vpsgFallbackCause);
            attempt = new MapRecognitionAttempt
            {
                Diagnostics = attempt.Diagnostics,
                Recognition = attempt.Recognition,
                Choices = attempt.Choices,
                FailureReason = attempt.FailureReason,
                StructureResult = attempt.StructureResult,
                GateDetectionResult = attempt.GateDetectionResult,
                StructureAttempted = attempt.StructureAttempted,
                StructureAccepted = attempt.StructureAccepted,
                StructureFailureReason = attempt.StructureFailureReason,
                SearchStage = attempt.SearchStage,
                Status = fallbackFailureStatus
            };
        }

        return attempt;
    }
}
