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

    /// <summary>Estimates locked-floor VPSG scale, then validates structure.</summary>
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

        var referencePath = Repository.GetFloorRecognitionPath(map, floorKey);
        using var reference = Cv2.ImRead(referencePath, ImreadModes.Grayscale);
        if (reference.Empty())
        {
            return MapCvRecognitionDiagnostics.Failure(
                diagnostics,
                "The locked floor recognition image could not be read.");
        }
        var stopwatch = Stopwatch.StartNew();
        LockedFloorFeatureFit? fit = null;
        MapScaleEstimationEvidence? scaleEvidence = null;
        var usedVpsg = false;
        var liveFrameCacheHit = false;
        var liveStructureExtractionMilliseconds = 0d;
        string rejectionReason;
        try
        {
            using var preparedReference = _structureCache.GetOrCreate(
                map.Id,
                map.UpdatedAt,
                reference,
                profile.WholeImageIgnoreRegions,
                floorKey,
                structureTuning.Generation);
            var preparedLive = frame.GetOrCreateDefaultLiveStructureFeatures(
                _structurePreprocessor,
                MapStructurePreprocessingProfile.EdgesAndFeatures,
                out liveFrameCacheHit,
                out var originalLiveStructureExtractionMilliseconds,
                out _,
                generateVisibleMask: structureTuning.EnableVisibleMask,
                generationTuning: structureTuning.Generation);
            liveStructureExtractionMilliseconds = liveFrameCacheHit
                ? 0d
                : originalLiveStructureExtractionMilliseconds;
            var scaleGraph = _vpsgScaleGraphCache.GetOrCreate(
                map,
                floorKey,
                reference.Size(),
                preparedReference.KeyPoints);
            if (_vpsgScaleEstimator.TryEstimate(
                    preparedReference,
                    preparedLive,
                    scaleGraph,
                    scaleSeed.ScaleX,
                    out var estimate,
                    out var vpsgRejection)
                && estimate is not null)
            {
                usedVpsg = true;
                scaleEvidence = new MapScaleEstimationEvidence
                {
                    UniqueMatches = estimate.Evidence.UniqueMatches,
                    PairVotes = estimate.Evidence.PairVotes,
                    ReferenceSpan = estimate.Evidence.ReferenceSpan,
                    LiveSpan = estimate.Evidence.LiveSpan
                        * frame.PhysicalPixelsPerComputationPixel,
                    ResidualPixels = estimate.Evidence.ResidualPixels
                        * frame.PhysicalPixelsPerComputationPixel,
                    RotationDegrees = estimate.Evidence.RotationDegrees,
                    RelativeMedianAbsoluteDeviation =
                        estimate.Evidence.RelativeMedianAbsoluteDeviation
                };
                fit = new LockedFloorFeatureFit(
                    estimate.Scale * frame.PhysicalPixelsPerComputationPixel,
                    estimate.OffsetX * frame.PhysicalPixelsPerComputationPixel,
                    estimate.OffsetY * frame.PhysicalPixelsPerComputationPixel,
                    estimate.Evidence.UniqueMatches,
                    estimate.Evidence.ResidualPixels
                        * frame.PhysicalPixelsPerComputationPixel,
                    estimate.Evidence.ReferenceSpan,
                    estimate.Evidence.LiveSpan
                        * frame.PhysicalPixelsPerComputationPixel,
                    0d,
                    estimate.Confidence);
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
                    reference,
                    frame.Image,
                    double.NaN,
                    out var siftRejection);
                rejectionReason = $"VPSG: {vpsgRejection}; SIFT: {siftRejection}";
            }
            else
            {
                fit = null;
                rejectionReason = vpsgRejection;
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
                    ["referenceWidth"] = reference.Width,
                    ["referenceHeight"] = reference.Height,
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

        var validationSeed = MapFeatureCacheRules.CreateScaleSeed(
            map,
            floorKey,
            fit.Scale,
            frame.ViewportBounds.X + fit.OffsetX,
            frame.ViewportBounds.Y + fit.OffsetY);
        var validation = AlignWithCachedScale(
            frame,
            selectedMapId,
            floorKey,
            validationSeed,
            alignmentMode,
            tuning,
            structureTuning,
            identityPriorConfidence);
        validation.Diagnostics.ScaleBootstrapAttempted = true;
        validation.Diagnostics.ScaleBootstrapSucceeded = usedVpsg;
        validation.Diagnostics.ScaleBootstrapValidated =
            usedVpsg && validation.Recognition is not null;
        validation.Diagnostics.ScaleBootstrapScale = fit.Scale;
        validation.Diagnostics.ScaleBootstrapConfidence = fit.Confidence;
        validation.Diagnostics.ScaleBootstrapUniqueMatches = fit.InlierCount;
        validation.Diagnostics.ScaleBootstrapPairVotes =
            scaleEvidence?.PairVotes ?? 0;
        validation.Diagnostics.ScaleBootstrapResidualPixels = fit.Residual;
        validation.Diagnostics.ScaleBootstrapRelativeMad =
            scaleEvidence?.RelativeMedianAbsoluteDeviation ?? 0d;
        validation.Diagnostics.LiveStructurePreprocessMilliseconds +=
            liveStructureExtractionMilliseconds;
        validation.Diagnostics.StructurePreprocessMilliseconds +=
            liveStructureExtractionMilliseconds;
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            validation.Recognition is null
                ? MapLogLevel.Warning
                : MapLogLevel.Info,
            $"VPSG 缩放预处理{(usedVpsg ? string.Empty : "兼容回退")} "
            + $"· map={map.SequenceNumber}#{floorKey} · scale={fit.Scale:F5} "
            + $"· matches={fit.InlierCount} · residual={fit.Residual:F2}px "
            + $"· confidence={fit.Confidence:P0} "
            + $"· structureValidation={(validation.Recognition is null ? "rejected" : "accepted")}",
            elapsedMs: stopwatch.Elapsed.TotalMilliseconds,
            details: new()
            {
                ["mapId"] = map.Id,
                ["floor"] = floorKey,
                ["scale"] = fit.Scale,
                ["inliers"] = fit.InlierCount,
                ["residual"] = fit.Residual,
                ["referenceSpan"] = fit.ReferenceSpan,
                ["liveSpan"] = fit.LiveSpan,
                ["averageDescriptorDistance"] =
                    fit.AverageDescriptorDistance,
                ["featureConfidence"] = fit.Confidence,
                ["identityPriorConfidence"] = identityPriorConfidence,
                ["vpsg"] = usedVpsg,
                ["pairVotes"] = scaleEvidence?.PairVotes ?? 0,
                ["relativeMad"] =
                    scaleEvidence?.RelativeMedianAbsoluteDeviation ?? 0d,
                ["liveFrameCacheHit"] = liveFrameCacheHit,
                ["liveStructureExtractionMs"] =
                    liveStructureExtractionMilliseconds,
                ["structureValidationAccepted"] =
                    validation.Recognition is not null,
                ["structureValidationFailure"] = validation.FailureReason
            });
        return validation;
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
/*
 * 文件职责：MapCvRecognitionService.LockedFloorFeature。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
