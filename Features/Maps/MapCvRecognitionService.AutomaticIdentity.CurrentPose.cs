namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal MapRecognitionAttempt AlignAutomaticIdentityCurrentFrame(
        CapturedGameFrame frame, Guid mapId, string floorKey,
        MapRecognitionTuning tuning, MapStructureRegistrationTuning validation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var map = TryGetMap(mapId);
        if (map is null || !TryGetVpsg3FloorLease(map, floorKey, out var lease)
            || lease is null)
            return MapCvRecognitionDiagnostics.Failure(
                MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount),
                "当前地图楼层的结构索引尚未就绪。");
        using (lease)
        {
            // Reused identity is independent of pose. Estimate this floor's
            // scale from this current frame, with no old-frame scale seed.
            var observation = frame.GetOrCreateVpsg3Observation();
            var proposal = Vpsg3FastBootstrapSolver.TrySolve(
                observation, lease.Floor, knownScaleSeed: null);
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return CurrentPoseFailure("当前画面贴合超过时间预算。");
            var item = new AutomaticIdentityFloorWork(new(map, floorKey, true, string.Empty))
                { Lease = lease };
            var strict = validation.Clone();
            strict.Mode = MapStructureRegistrationMode.ScanVerification;
            strict.ReusePreviousAlignmentResult = false;
            strict.EnableDebugOutput = false;
            strict.Normalize();
            var input = ExtractAutomaticLocalGeometry(frame, observation, tuning);
            if (!proposal.IsAccepted)
            {
                // Geometry is an independent proposal, never a fabricated
                // successful periodic scale or a reuse of the old frame pose.
                var search = MapLocalGeometrySolver.Solve(observation, lease.Floor,
                    input.Corners, AutomaticLocalGeometryAnchors(item), input.Gates,
                    cancellationToken);
                if (!search.Complete || search.Poses.Count == 0)
                    return CurrentPoseFailure(search.FailureReason);
                item.GeometryReferenceGeometry = search.ReferenceGeometry;
                var candidates = search.Poses.Where(pose => pose.Spatial.IsSpatiallyConsistent
                    && pose.WeightedScore >= Vpsg3TuningConfig.Default.MinVerificationScore).ToArray();
                if (candidates.Length == 0)
                    return CurrentPoseFailure("当前入口结构支持不足。");
                var checkedPoses = ValidateAutomaticGeometryPoses(frame, observation, item,
                    input.Corners, candidates, tuning, strict, cancellationToken);
                var evidence = new List<AutomaticIdentityPoseEvidence>();
                foreach (var checkedPose in item.GeometryValidations)
                {
                    var added = AddAutomaticPoseEvidence(evidence, observation, item,
                        checkedPose.Attempt, checkedPose.FinalPose is not null);
                    if (!IsCompletedAutomaticIdentityPoseValidation(checkedPose.Attempt)
                        || (checkedPose.Attempt.StructureAccepted && !added))
                        return CurrentPoseFailure("当前入口结构的竞争位姿尚未完成验证。");
                }
                evidence = ExcludeContradictedAutomaticPoses(observation, evidence,
                    strict.MinimumSpanPixels, input, cancellationToken);
                var selected = SelectAutomaticPose(observation, evidence);
                if (selected is null)
                    return CurrentPoseFailure("最终贴合与入口几何提案不一致或位姿不唯一。");
                cancellationToken.ThrowIfCancellationRequested();
                if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                    return CurrentPoseFailure("当前画面贴合超过时间预算。");
                return selected.Attempt;
            }
            var result = ValidateAutomaticIdentityPose(frame, item,
                proposal.Scale, proposal.OffsetX, proposal.OffsetY,
                proposal.Confidence, tuning, strict);
            if (result.StructureAccepted)
            {
                var evidence = new List<AutomaticIdentityPoseEvidence>();
                if (!AddAutomaticPoseEvidence(evidence, observation, item, result, canAccept: true)
                    || ExcludeContradictedAutomaticPoses(observation, evidence,
                        strict.MinimumSpanPixels, input, cancellationToken).Count == 0)
                    return CurrentPoseFailure("最终贴合与当前可见室内结构或入口位置矛盾。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return CurrentPoseFailure("当前画面贴合超过时间预算。");
            return result;
        }
    }

    private MapRecognitionAttempt CurrentPoseFailure(string reason) =>
        MapCvRecognitionDiagnostics.Failure(
            MapCvRecognitionDiagnostics.CreateDiagnostics(ReadyMapCount, TotalMapCount),
            $"当前帧位姿未通过验证：{reason}");
}
