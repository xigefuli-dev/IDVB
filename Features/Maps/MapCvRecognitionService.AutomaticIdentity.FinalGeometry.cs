using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private (MapRecognitionAttempt Attempt, MapLocalGeometryPose? FinalPose,
        MapLocalGeometryPose Proposal) ValidateAutomaticGeometryPoses(
        CapturedGameFrame frame, Vpsg3LiveObservation observation,
        AutomaticIdentityFloorWork item, IReadOnlyList<MapLocalCornerGeometry> corners,
        IReadOnlyList<MapLocalGeometryPose> candidates, MapRecognitionTuning tuning,
        MapStructureRegistrationTuning validation, CancellationToken cancellationToken)
    {
        MapRecognitionAttempt? first = null;
        foreach (var proposal in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return (CurrentPoseFailure("当前画面贴合超过时间预算。"), null, candidates[0]);
            var attempt = ValidateAutomaticIdentityPose(frame, item, proposal.Scale,
                proposal.OffsetX, proposal.OffsetY, proposal.WeightedScore, tuning, validation);
            first ??= attempt;
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return (CurrentPoseFailure("当前画面贴合超过时间预算。"), null, candidates[0]);
            var identityBound = attempt.Recognition is { } recognized
                && recognized.Map.Id == item.Map.Id
                && string.Equals(recognized.Result.Floor, item.FloorKey,
                    StringComparison.OrdinalIgnoreCase);
            var final = identityBound ? VerifyAutomaticGeometryFinalPose(observation,
                item.Lease!.Floor, corners, attempt, proposal, item.GeometryReferenceGeometry) : null;
            var transform = attempt.Recognition?.Result.OverlayTransform;
            var finalCorners = transform is null ? 0 : MapLocalGeometrySolver.CountPoseMatches(
                observation, item.Lease!.Floor, corners,
                transform.ScaleX, transform.OffsetX, transform.OffsetY,
                item.GeometryReferenceGeometry).Count;
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                "自动入口几何最终位姿验证", details: new()
                {
                    ["mapId"] = item.Map.Id,
                    ["floor"] = item.FloorKey,
                    ["proposal"] = proposal,
                    ["strictTransform"] = transform,
                    ["strictAccepted"] = attempt.StructureAccepted,
                    ["finalCorners"] = finalCorners,
                    ["finalAccepted"] = final is not null
                });
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return (CurrentPoseFailure("当前画面贴合超过时间预算。"), null, candidates[0]);
            item.GeometryValidations.Add(new(proposal, attempt, final));
            if (final is not null)
            {
                foreach (var runner in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AutomaticGeometryPosesAgree(observation, final, runner)
                        || final.Spatial.GlobalScore - runner.Spatial.GlobalScore
                            >= Vpsg3TuningConfig.Default.MinApertureMargin
                        || item.GeometryValidations.Any(checkedPose => checkedPose.Proposal == runner))
                        continue;
                    if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                        return (CurrentPoseFailure("当前画面贴合超过时间预算。"), null, proposal);
                    var runnerAttempt = ValidateAutomaticIdentityPose(frame, item, runner.Scale,
                        runner.OffsetX, runner.OffsetY, runner.WeightedScore, tuning, validation);
                    var runnerIdentityBound = runnerAttempt.Recognition is { } runnerRecognition
                        && runnerRecognition.Map.Id == item.Map.Id
                        && string.Equals(runnerRecognition.Result.Floor, item.FloorKey,
                            StringComparison.OrdinalIgnoreCase);
                    var runnerFinal = runnerIdentityBound ? VerifyAutomaticGeometryFinalPose(observation,
                        item.Lease!.Floor, corners, runnerAttempt, runner, item.GeometryReferenceGeometry) : null;
                    item.GeometryValidations.Add(new(runner, runnerAttempt, runnerFinal));
                    MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                        "自动入口几何竞争位姿验证", details: new()
                        {
                            ["mapId"] = item.Map.Id,
                            ["floor"] = item.FloorKey,
                            ["proposal"] = runner,
                            ["strictTransform"] = runnerAttempt.Recognition?.Result.OverlayTransform,
                            ["strictAccepted"] = runnerAttempt.StructureAccepted,
                            ["qualityRejected"] = IsCompletedAutomaticIdentityQualityRejection(runnerAttempt),
                            ["rejection"] = runnerAttempt.StructureResult?.RejectionReason.ToString(),
                            ["finalPose"] = runnerFinal
                        });
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                        return (CurrentPoseFailure("当前画面贴合超过时间预算。"), null, proposal);
                }
                return (attempt, final, proposal);
            }
        }
        return (first!, null, candidates[0]);
    }

    private static MapLocalGeometryPose? VerifyAutomaticGeometryFinalPose(
        Vpsg3LiveObservation observation, Vpsg3PreparedFloor floor,
        IReadOnlyList<MapLocalCornerGeometry> corners, MapRecognitionAttempt strict,
        MapLocalGeometryPose proposal,
        IReadOnlyList<MapLocalCornerGeometry>? referenceGeometry = null)
    {
        var transform = strict.Recognition?.Result.OverlayTransform;
        if (!strict.StructureAccepted || transform is null
            || strict.Recognition!.Result.WasForcedBestResult
            || !double.IsFinite(transform.ScaleX) || transform.ScaleX <= 0
            || !double.IsFinite(transform.ScaleY)
            || !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY)
            || Math.Abs(transform.ScaleX - transform.ScaleY) > 1e-6)
            return null;
        // Six measured corners prove the proposed map identity. The existing
        // structure registrar authorizes the display pose independently; its
        // integer ROI sampling can move a sound pose by a fraction of a pixel.
        // Bind that authorization to this exact same-floor proposal instead
        // of treating a small raster adjustment as lost identity evidence.
        var identityMatches = MapLocalGeometrySolver.CountPoseMatches(observation, floor, corners,
            proposal.Scale, proposal.OffsetX, proposal.OffsetY, referenceGeometry);
        if (identityMatches.Count < 6) return null;
        foreach (var point in new[] { new Point2d(observation.ViewportBounds.X,
            observation.ViewportBounds.Y), new Point2d(observation.ViewportBounds.X + observation.Width,
                observation.ViewportBounds.Y + observation.Height) })
        {
            var proposed = new Point2d((point.X - proposal.OffsetX) / proposal.Scale,
                (point.Y - proposal.OffsetY) / proposal.Scale);
            var actual = new Point2d((point.X - transform.OffsetX) / transform.ScaleX,
                (point.Y - transform.OffsetY) / transform.ScaleY);
            if (double.Hypot(actual.X - proposed.X, actual.Y - proposed.Y)
                * Math.Max(proposal.Scale, transform.ScaleX)
                    > Vpsg3LiveObservation.AutomaticPoseTolerancePixels) return null;
        }
        var matched = MapLocalGeometrySolver.CountPoseMatches(observation, floor, corners,
            transform.ScaleX, transform.OffsetX, transform.OffsetY, referenceGeometry);
        var score = Vpsg3LocalRefiner.EvaluateScore(observation.SparseEdgePoints, floor,
            transform.ScaleX, transform.OffsetX, transform.OffsetY, observation.ViewportBounds);
        var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
            observation.SparseEdgePoints, observation.ValidMask, floor,
            transform.ScaleX, transform.OffsetX, transform.OffsetY,
            observation.ViewportBounds, observation.Width, observation.Height);
        if (score < Vpsg3TuningConfig.Default.MinVerificationScore || !spatial.IsSpatiallyConsistent)
            return null;
        return new(transform.ScaleX, transform.OffsetX, transform.OffsetY,
            matched.Count, matched.Error, score, spatial);
    }
}
