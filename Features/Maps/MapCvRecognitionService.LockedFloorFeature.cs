using OpenCvSharp;
using OpenCvSharp.Features2D;
using System.Diagnostics;

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
            var vpsgMode = Enum.IsDefined(structureTuning.VpsgScaleMode)
                ? structureTuning.VpsgScaleMode
                : VpsgScaleMode.Structure;
            var preprocessingProfile = vpsgMode == VpsgScaleMode.Structure
                ? MapStructurePreprocessingProfile.EdgesOnly
                : MapStructurePreprocessingProfile.EdgesAndFeatures;
            using var residentReferenceLease = _structureCache.TryRentResident(
                map.Id,
                map.UpdatedAt,
                floorKey,
                structureTuning.Generation,
                preprocessingProfile);
            MapStructureFeatures? ownedPreparedReference = null;
            Mat? decodedReference = null;
            var referenceLoadMilliseconds = 0d;
            if (residentReferenceLease is null)
            {
                var referencePath = Repository.GetFloorRecognitionPath(
                    map,
                    floorKey);
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
                    preprocessingProfile);
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
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked floor did not produce reliable feature geometry.");
        }

        // Once a bounded scan VPSG stage has started and produced scale basins,
        // its formal validation owns the stage budget. The outer scan clock
        // still decides whether this stage starts and whether another map runs.
        using var scanValidationBudget = structureTuning.Mode
                == MapStructureRegistrationMode.ScanVerification
            ? MapNoDoorAlignmentBudgetContext.Enter(
                () => structureTuning.StructureFallbackBudgetMilliseconds)
            : null;
        return ValidateVpsgScaleCandidates(
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
    }

    private static LockedFloorFeatureFit? TryFitLockedFloorFeature(
        Mat reference,
        Mat live,
        double priorScale,
        out string rejectionReason)
    {
        rejectionReason = string.Empty;
        using var liveGray = new Mat();
        switch (live.Channels())
        {
            case 1:
                live.CopyTo(liveGray);
                break;
            case 4:
                Cv2.CvtColor(live, liveGray, ColorConversionCodes.BGRA2GRAY);
                break;
            default:
                Cv2.CvtColor(live, liveGray, ColorConversionCodes.BGR2GRAY);
                break;
        }
        using var sift = SIFT.Create(
            nFeatures: 5000,
            nOctaveLayers: 3,
            contrastThreshold: 0.01,
            edgeThreshold: 15d,
            sigma: 1.6d);
        using var referenceDescriptors = new Mat();
        using var liveDescriptors = new Mat();
        sift.DetectAndCompute(
            reference,
            null,
            out var referencePoints,
            referenceDescriptors);
        sift.DetectAndCompute(
            liveGray,
            null,
            out var livePoints,
            liveDescriptors);
        if (referenceDescriptors.Empty() || liveDescriptors.Empty())
        {
            rejectionReason = "no SIFT descriptors";
            return null;
        }

        using var matcher = new BFMatcher(NormTypes.L2);
        var groups = matcher.KnnMatch(
            referenceDescriptors,
            liveDescriptors,
            2);
        var votes = new List<LockedFloorFeatureVote>();
        foreach (var group in groups)
        {
            if (group.Length < 2 || group[0].Distance >= group[1].Distance * 0.82d)
                continue;
            var match = group[0];
            var referencePoint = referencePoints[match.QueryIdx];
            var livePoint = livePoints[match.TrainIdx];
            var scale = livePoint.Size / Math.Max(0.01d, referencePoint.Size);
            if (scale is < 0.25d or > 2d)
                continue;
            if (double.IsFinite(priorScale)
                && priorScale > 0d
                && Math.Abs((scale / priorScale) - 1d)
                    > LockedFloorFeatureMaximumScaleChange)
            {
                continue;
            }
            votes.Add(new LockedFloorFeatureVote(
                scale,
                livePoint.Pt.X - (referencePoint.Pt.X * scale),
                livePoint.Pt.Y - (referencePoint.Pt.Y * scale),
                match.QueryIdx,
                match.TrainIdx,
                match.Distance));
        }
        using var crossMatcher = new BFMatcher(NormTypes.L2, crossCheck: true);
        foreach (var match in crossMatcher.Match(
                     referenceDescriptors,
                     liveDescriptors))
        {
            if (match.Distance >= 250d)
                continue;
            var referencePoint = referencePoints[match.QueryIdx];
            var livePoint = livePoints[match.TrainIdx];
            var scale = livePoint.Size / Math.Max(0.01d, referencePoint.Size);
            if (scale is < 0.25d or > 2d
                || (double.IsFinite(priorScale)
                    && priorScale > 0d
                    && Math.Abs((scale / priorScale) - 1d)
                        > LockedFloorFeatureMaximumScaleChange))
            {
                continue;
            }
            votes.Add(new LockedFloorFeatureVote(
                scale,
                livePoint.Pt.X - (referencePoint.Pt.X * scale),
                livePoint.Pt.Y - (referencePoint.Pt.Y * scale),
                match.QueryIdx,
                match.TrainIdx,
                match.Distance));
        }
        votes = votes
            .GroupBy(vote => (vote.ReferenceIndex, vote.LiveIndex))
            .Select(group => group.MinBy(vote => vote.DescriptorDistance)!)
            .ToList();
        if (votes.Count < LockedFloorFeatureMinimumInliers)
        {
            rejectionReason = $"only {votes.Count} scale-consistent descriptor votes";
            return null;
        }

        var clusters = votes
            .Select(seed => votes
                .Where(vote =>
                    Math.Abs(Math.Log(vote.Scale / seed.Scale)) < 0.08d
                    && Math.Sqrt(
                        Math.Pow(vote.OffsetX - seed.OffsetX, 2d)
                        + Math.Pow(vote.OffsetY - seed.OffsetY, 2d)) < 35d)
                .OrderBy(vote => vote.DescriptorDistance)
                .DistinctBy(vote => vote.ReferenceIndex)
                .DistinctBy(vote => vote.LiveIndex)
                .ToArray())
            .ToList();
        for (var firstIndex = 0; firstIndex < votes.Count - 1; firstIndex++)
        {
            var first = votes[firstIndex];
            var firstReference = referencePoints[first.ReferenceIndex].Pt;
            var firstLive = livePoints[first.LiveIndex].Pt;
            for (var secondIndex = firstIndex + 1;
                 secondIndex < votes.Count;
                 secondIndex++)
            {
                var second = votes[secondIndex];
                if (first.ReferenceIndex == second.ReferenceIndex
                    || first.LiveIndex == second.LiveIndex)
                {
                    continue;
                }
                var secondReference = referencePoints[second.ReferenceIndex].Pt;
                var secondLive = livePoints[second.LiveIndex].Pt;
                var referenceDeltaX = secondReference.X - firstReference.X;
                var referenceDeltaY = secondReference.Y - firstReference.Y;
                var liveDeltaX = secondLive.X - firstLive.X;
                var liveDeltaY = secondLive.Y - firstLive.Y;
                var referenceLengthSquared =
                    (referenceDeltaX * referenceDeltaX)
                    + (referenceDeltaY * referenceDeltaY);
                if (referenceLengthSquared < 6400d)
                    continue;
                var pairScale =
                    (double)((referenceDeltaX * liveDeltaX)
                    + (referenceDeltaY * liveDeltaY))
                    / referenceLengthSquared;
                if (pairScale is < 0.25d or > 2d
                    || (double.IsFinite(priorScale)
                        && priorScale > 0d
                        && Math.Abs((pairScale / priorScale) - 1d)
                            > LockedFloorFeatureMaximumScaleChange))
                {
                    continue;
                }
                var pairOffsetX =
                    ((firstLive.X - (firstReference.X * pairScale))
                    + (secondLive.X - (secondReference.X * pairScale))) / 2d;
                var pairOffsetY =
                    ((firstLive.Y - (firstReference.Y * pairScale))
                    + (secondLive.Y - (secondReference.Y * pairScale))) / 2d;
                var pairError = Math.Sqrt(
                    Math.Pow(firstLive.X - (pairOffsetX + (firstReference.X * pairScale)), 2d)
                    + Math.Pow(firstLive.Y - (pairOffsetY + (firstReference.Y * pairScale)), 2d)
                    + Math.Pow(secondLive.X - (pairOffsetX + (secondReference.X * pairScale)), 2d)
                    + Math.Pow(secondLive.Y - (pairOffsetY + (secondReference.Y * pairScale)), 2d));
                if (pairError > LockedFloorFeatureClusterTolerance)
                    continue;
                clusters.Add(votes
                    .Where(vote =>
                    {
                        var referencePoint = referencePoints[vote.ReferenceIndex].Pt;
                        var livePoint = livePoints[vote.LiveIndex].Pt;
                        return Math.Sqrt(
                            Math.Pow(livePoint.X - (pairOffsetX + (referencePoint.X * pairScale)), 2d)
                            + Math.Pow(livePoint.Y - (pairOffsetY + (referencePoint.Y * pairScale)), 2d))
                            <= LockedFloorFeatureClusterTolerance;
                    })
                    .OrderBy(vote => vote.DescriptorDistance)
                    .DistinctBy(vote => vote.ReferenceIndex)
                    .DistinctBy(vote => vote.LiveIndex)
                    .ToArray());
            }
        }
        var fit = clusters
            .Where(cluster =>
                cluster.Length >= LockedFloorFeatureMinimumInliers)
            .Select(cluster => TryFitLockedFloorFeatureCluster(
                cluster,
                referencePoints,
                livePoints,
                priorScale))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderByDescending(candidate => candidate.InlierCount)
            .ThenByDescending(candidate => candidate.ReferenceSpan)
            .ThenBy(candidate => candidate.Residual)
            .FirstOrDefault();
        if (fit is null)
        {
            var largestCluster = clusters.Max(cluster => cluster.Length);
            rejectionReason = $"no reliable uniform fit from {clusters.Count} clusters; largest={largestCluster}, votes={votes.Count}";
            return null;
        }
        return fit;
    }
}
