using OpenCvSharp;
using MapLocalCorner = IDVBuff.Features.Maps.MapLocalCornerGeometry;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private sealed record AutomaticGeometryInput(IReadOnlyList<MapLocalCorner> Corners,
        IReadOnlyList<Point2d> Gates, GateDetectionResult GateDetection);

    private AutomaticGeometryInput
        ExtractAutomaticLocalGeometry(CapturedGameFrame frame,
            Vpsg3LiveObservation observation, MapRecognitionTuning tuning)
    {
        // Use the current extractor's strong contours to propose physical
        // corner correspondences. Its weaker fog edges still participate in
        // the solver's full-wall scoring and the strict alignment validator.
        var corners = MapLocalCornerGeometryExtractor.ExtractCorners(observation.ProposalEdges);
        using var image = GateTemplateDetector.CreateMatchImage(frame.Image);
        var detected = _gateDetector.Detect(image, frame.ViewportBounds,
            frame.ClientBounds.Width, tuning.GateTemplateThreshold,
            new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
                AllowDualGateEarlyExit = false,
                AllowSingleGateEarlyExit = false
            });
        Point2d[] gates = detected.BudgetExceeded ? [] : detected.Gates.Select(gate =>
            new Point2d(gate.ScreenBounds.CenterX - observation.ViewportBounds.X,
                gate.ScreenBounds.CenterY - observation.ViewportBounds.Y)).ToArray();
        return new(corners, gates, detected);
    }

    private static IReadOnlyList<NormalizedRectangle> AutomaticLocalGeometryAnchors(
        AutomaticIdentityFloorWork item) =>
        MapFloorRules.GetFloorProfile(item.Map, item.FloorKey)?.Anchors
            .Where(anchor => anchor.Bounds?.IsValid == true
                && (anchor.Key == "main-entrance" || anchor.Key == "side-entrance"
                    || anchor.Key == "second-floor-primary"))
            .Select(anchor => anchor.Bounds!.Clone()).ToArray() ?? [];

    private MapAutomaticIdentityAttempt RecognizeAutomaticLocalGeometry(
        CapturedGameFrame frame, Vpsg3LiveObservation observation,
        AutomaticIdentityFloorWork[] work, AutomaticIdentityRun run,
        MapCatalogRevision snapshotRevision,
        MapRecognitionTuning tuning, MapStructureRegistrationTuning validation,
        CancellationToken cancellationToken, AutomaticGeometryInput input,
        List<AutomaticIdentityPoseEvidence> evidence,
        HashSet<AutomaticIdentityFloorWork> nativeBlockers,
        MapCatalogSnapshot identityCatalog)
    {
        MapDiagnosticModeCapture.WriteInputs(frame.Image, observation.ProposalEdges,
            tag: "automatic-entry-geometry");
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
            MapLogLevel.Info, "自动入口墙角几何输入", details: new()
            {
                ["cornerCount"] = input.Corners.Count,
                ["corners"] = input.Corners.Select(corner => new
                {
                    corner.Point.X, corner.Point.Y,
                    corner.RayAAngleDegrees, corner.RayBAngleDegrees
                }).ToArray(),
                ["gates"] = input.Gates.Select(gate => new { gate.X, gate.Y }).ToArray(),
                ["viewport"] = frame.ViewportBounds
            });
        // The caller already measured every native strict pose against this
        // same observation and these still-live floor leases. Carry the full
        // list, including defeated poses, into the combined competition;
        // native-only survivors must not replace native-plus-geometry rivals.
        var completed = 0;
        foreach (var item in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.GeometryAttempted = true;
            var geometryTimer = System.Diagnostics.Stopwatch.StartNew();
            var search = MapLocalGeometrySolver.Solve(observation, item.Lease!.Floor,
                input.Corners, AutomaticLocalGeometryAnchors(item), input.Gates, cancellationToken);
            geometryTimer.Stop();
            item.GeometryQueryCorners = search.QueryCorners;
            item.GeometryReferenceCorners = search.ReferenceCorners;
            item.GeometryPoseCount = search.Poses.Count;
            item.GeometryComparisonComplete = search.Complete;
            item.GeometryReferenceGeometry = search.ReferenceGeometry;
            item.BlocksAcceptance = !search.Complete || nativeBlockers.Contains(item);
            MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                MapLogLevel.Info, "自动入口墙角几何比较", details: new()
                {
                    ["mapId"] = item.Map.Id,
                    ["floor"] = item.FloorKey,
                    ["queryCorners"] = search.QueryCorners,
                    ["referenceCorners"] = search.ReferenceCorners,
                    ["gateCount"] = input.Gates.Count,
                    ["complete"] = search.Complete,
                    ["poseCount"] = search.Poses.Count,
                    ["elapsedMs"] = geometryTimer.Elapsed.TotalMilliseconds,
                    ["best"] = search.Poses.FirstOrDefault(),
                    ["poses"] = search.Poses,
                    ["failureReason"] = search.FailureReason
                });
            if (!search.Complete)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.InsufficientEvidence;
                item.FailureReason = search.FailureReason;
                continue;
            }
            completed++;
            var candidates = search.Poses.Where(pose =>
                pose.WeightedScore >= Vpsg3TuningConfig.Default.MinVerificationScore
                && pose.Spatial.IsSpatiallyConsistent).ToArray();
            if (candidates.Length == 0)
            {
                if (nativeBlockers.Contains(item))
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.HighSupportUnresolved;
                    item.BlocksAcceptance = true;
                    item.FailureReason = "入口墙角没有排除此前具有结构支持的竞争位姿。";
                    continue;
                }
                item.Status = evidence.Any(pose => pose.Owner == item)
                    ? MapAutomaticIdentityCandidateStatus.StrictlyVerified
                    : MapAutomaticIdentityCandidateStatus.StrictValidationRejected;
                item.FailureReason = "完整入口墙角比较没有产生足够结构支持的位姿。";
                continue;
            }
            item.StrictValidationAttempted = true;
            var checkedPoses = ValidateAutomaticGeometryPoses(frame, observation, item,
                input.Corners, candidates, tuning, validation, cancellationToken);
            var best = checkedPoses.Proposal;
            item.GeometryMatchedCorners = best.CornerCount;
            item.HasCrediblePoseProposal = true;
            item.HasHighPhysicalSupport = true;
            item.ProposalScale = best.Scale;
            item.ProposalOffsetX = best.OffsetX;
            item.ProposalOffsetY = best.OffsetY;
            item.GeometryWeightedScore = best.WeightedScore;
            var strict = checkedPoses.Attempt;
            item.AlignmentAttempt = strict;
            item.StrictRejectionReason = strict.StructureResult?.RejectionReason;
            item.StrictValidationAccepted = strict.StructureAccepted
                && strict.Recognition?.Result.OverlayTransform is not null
                && !strict.Recognition.Result.WasForcedBestResult;
            var finalPose = checkedPoses.FinalPose;
            item.StrictValidationAccepted &= finalPose is not null;
            foreach (var checkedPose in item.GeometryValidations)
            {
                var hasEvidence = AddAutomaticPoseEvidence(evidence, observation, item,
                    checkedPose.Attempt, checkedPose.FinalPose is not null, out var contradicted);
                if (!IsCompletedAutomaticIdentityPoseValidation(checkedPose.Attempt)
                    || (checkedPose.Attempt.StructureAccepted && !hasEvidence && !contradicted))
                    item.BlocksAcceptance = true;
            }
            if (item.StrictValidationAccepted)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.StrictlyVerified;
                item.FailureReason = string.Empty;
            }
            else if (IsCompletedAutomaticIdentityQualityRejection(strict))
            {
                item.Status = MapAutomaticIdentityCandidateStatus.StrictValidationRejected;
                item.FailureReason = strict.StructureFailureReason;
            }
            else
            {
                item.Status = MapAutomaticIdentityCandidateStatus.HighSupportUnresolved;
                // A measured strict positive with no eligible six-corner
                // binding is a known rival, not an unknown pose. It can never
                // win, but may be excluded only by the full wall competition.
                item.BlocksAcceptance |= !IsCompletedAutomaticIdentityPoseValidation(strict);
                item.FailureReason = strict.StructureAccepted
                    ? "严格对齐后的实际位姿未保留独立墙角支持。" : strict.StructureFailureReason;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
            return run.Finish(MapAutomaticIdentityStatus.TimedOut, completed,
                "入口几何身份识别超过时间预算。");
        var surviving = RemoveDominatedAutomaticPoseIdentities(observation, evidence,
            validation.MinimumSpanPixels, cancellationToken, input);
        cancellationToken.ThrowIfCancellationRequested();
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
            return run.Finish(MapAutomaticIdentityStatus.TimedOut, completed,
                "入口几何身份竞争超过时间预算。");
        if (snapshotRevision != _catalogRevision
            || snapshotRevision != _repository.GetCatalogRevision())
            return run.Finish(MapAutomaticIdentityStatus.ResourcesPending, completed,
                "入口几何识别期间地图目录发生变化。");
        var identities = surviving.Select(pose => pose.Owner).Distinct().ToArray();
        foreach (var item in work)
        {
            if (evidence.Any(pose => pose.Owner == item)
                && !identities.Contains(item) && !item.BlocksAcceptance)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.DominatedByObservedStructure;
                item.FailureReason = "该楼层所有已验证竞争位姿均被同帧室内、入口或跨身份墙段证据排除。";
            }
        }
        var selection = SelectConfiguredAutomaticIdentityPose(observation, surviving,
            work.Where(item => item.BlocksAcceptance).ToArray(), identityCatalog);
        if (selection is { Pose: var accepted })
        {
            accepted.Owner.AlignmentAttempt = accepted.Attempt;
            accepted.Owner.StrictValidationAccepted = true;
            accepted.Owner.Status = MapAutomaticIdentityCandidateStatus.StrictlyVerified;
            return run.Finish(MapAutomaticIdentityStatus.Accepted, completed, string.Empty,
                accepted.Attempt, selection.ConfiguredGroup);
        }
        if (identities.Length > 1)
            return run.Finish(MapAutomaticIdentityStatus.Ambiguous, completed,
                "入口附近存在多个无法排除的地图或位姿。");
        if (work.Any(item => item.BlocksAcceptance))
            return run.Finish(MapAutomaticIdentityStatus.InsufficientEvidence, completed,
                "入口几何比较尚未提供完整且可区分的身份证据。");
        if (identities.Length == 1)
        {
            identities[0].Status = MapAutomaticIdentityCandidateStatus.LocalPoseAmbiguous;
            identities[0].BlocksAcceptance = true;
            identities[0].FailureReason = "身份竞争仅剩一个楼层，但严格验证的实际位姿仍未通过唯一性要求。";
            return run.Finish(MapAutomaticIdentityStatus.Ambiguous, completed, identities[0].FailureReason);
        }
        return run.Finish(MapAutomaticIdentityStatus.InsufficientEvidence, completed,
            "入口墙角未能唯一确认地图，后续开图将使用新画面自动重试。");
    }

    private static List<AutomaticIdentityPoseEvidence> CollectNativeAutomaticPoseEvidence(
        Vpsg3LiveObservation observation, AutomaticIdentityFloorWork[] work,
        out HashSet<AutomaticIdentityFloorWork> unresolved)
    {
        // Known strict positives compete as actual poses, including rejected
        // native-local winners. Missing runner work remains unresolved; a
        // completed quality rejection cannot create a permanent ambiguity flag.
        unresolved = work.Where(item => item.BlocksAcceptance
            && !item.NativePoseComparisonComplete
            && (item.HasHighPhysicalSupport || item.HasCrediblePoseProposal
                || item.StrictValidationAccepted)).ToHashSet();
        var evidence = new List<AutomaticIdentityPoseEvidence>();
        foreach (var item in work)
        {
            if (!AddAutomaticPoseEvidence(evidence, observation, item, item.AlignmentAttempt,
                    item.VpsgAccepted, out var primaryContradicted)
                && !primaryContradicted && item.AlignmentAttempt?.StructureAccepted == true)
                unresolved.Add(item);
            if (!AddAutomaticPoseEvidence(evidence, observation, item, item.RunnerUpAlignmentAttempt,
                    canAccept: false, out var runnerContradicted)
                && !runnerContradicted && item.RunnerUpAlignmentAttempt?.StructureAccepted == true)
                unresolved.Add(item);
        }
        return evidence;
    }

    private static bool AddAutomaticPoseEvidence(List<AutomaticIdentityPoseEvidence> evidence,
        Vpsg3LiveObservation observation, AutomaticIdentityFloorWork item,
        MapRecognitionAttempt? attempt, bool canAccept) =>
        AddAutomaticPoseEvidence(evidence, observation, item, attempt, canAccept, out _);

    private static bool AddAutomaticPoseEvidence(List<AutomaticIdentityPoseEvidence> evidence,
        Vpsg3LiveObservation observation, AutomaticIdentityFloorWork item,
        MapRecognitionAttempt? attempt, bool canAccept, out bool contradicted)
    {
        contradicted = false;
        var transform = attempt?.Recognition?.Result.OverlayTransform;
        if (attempt?.StructureAccepted != true || transform is null
            || attempt.Recognition!.Map.Id != item.Map.Id
            || !string.Equals(attempt.Recognition.Result.Floor, item.FloorKey,
                StringComparison.OrdinalIgnoreCase)
            || attempt.Recognition.Result.WasForcedBestResult
            || !double.IsFinite(transform.ScaleX) || transform.ScaleX <= 0
            || !double.IsFinite(transform.ScaleY) || transform.ScaleY <= 0
            || !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY)) return false;
        if (item.IndexedDomainSource is not null || item.IndexedWallWitnesses is not null)
        {
            if (item.IndexedDomainSource is not { } indexedSource
                || item.IndexedWallWitnesses is not { } witnesses
                || transform.OrientationDegrees != 0
                || Math.Abs(transform.ScaleX - transform.ScaleY) > 1e-6) return false;
            var wallCheck = MapFrontEntryDomainSearch.CheckPose(indexedSource, witnesses, transform.ScaleX,
                    transform.OffsetX - observation.ViewportBounds.X,
                    transform.OffsetY - observation.ViewportBounds.Y, CancellationToken.None,
                    () => MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0);
            if (wallCheck.Support != MapFrontEntryPoseSupport.Supported)
            {
                contradicted = wallCheck.Support == MapFrontEntryPoseSupport.Contradicted;
                MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
                    "自动入口具体位姿墙段核验", details: new()
                    {
                        ["mapId"] = item.Map.Id, ["floor"] = item.FloorKey,
                        ["transform"] = transform, ["support"] = wallCheck.Support.ToString(),
                        ["testedWitnesses"] = wallCheck.TestedWitnesses,
                        ["unsupportedWitnesses"] = wallCheck.UnsupportedWitnesses,
                        ["firstUnsupportedWitness"] = wallCheck.FirstUnsupportedWitness is { } point
                            ? new { point.X, point.Y } : null
                    });
                return false;
            }
        }
        var score = Vpsg3LocalRefiner.EvaluateScore(observation.SparseEdgePoints, item.Lease!.Floor,
            transform.ScaleX, transform.OffsetX, transform.OffsetY, observation.ViewportBounds);
        var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(observation.SparseEdgePoints,
            observation.ValidMask, item.Lease.Floor, transform.ScaleX, transform.OffsetX,
            transform.OffsetY, observation.ViewportBounds, observation.Width, observation.Height);
        var pose = new MapLocalGeometryPose(transform.ScaleX, transform.OffsetX,
            transform.OffsetY, 0, 0, score, spatial);
        evidence.Add(new(item, attempt, pose, canAccept));
        return true;
    }

    private static AutomaticIdentityPoseEvidence? SelectAutomaticPose(Vpsg3LiveObservation observation,
        IReadOnlyList<AutomaticIdentityPoseEvidence> evidence) => evidence.FirstOrDefault(candidate =>
        candidate.CanAccept && evidence.All(other => SameAutomaticIdentity(candidate.Owner, other.Owner)
            && (AutomaticGeometryPosesAgree(observation, candidate.Pose, other.Pose)
                || candidate.Pose.Spatial.GlobalScore - other.Pose.Spatial.GlobalScore
                    >= Vpsg3TuningConfig.Default.MinApertureMargin)));

    private static bool AutomaticGeometryPosesAgree(Vpsg3LiveObservation observation,
        MapOverlayTransform native, MapLocalGeometryPose geometric)
    {
        foreach (var point in new[] { new Point2d(observation.ViewportBounds.X,
            observation.ViewportBounds.Y), new Point2d(observation.ViewportBounds.X
                + observation.Width, observation.ViewportBounds.Y + observation.Height) })
        {
            var a = new Point2d((point.X - native.OffsetX) / native.ScaleX,
                (point.Y - native.OffsetY) / native.ScaleY);
            var b = new Point2d((point.X - geometric.OffsetX) / geometric.Scale,
                (point.Y - geometric.OffsetY) / geometric.Scale);
            if (double.Hypot(a.X - b.X, a.Y - b.Y) * Math.Max(native.ScaleX, geometric.Scale)
                >= Vpsg3TuningConfig.Default.MinDistinctDistance) return false;
        }
        return true;
    }

    private static bool AutomaticGeometryPosesAgree(Vpsg3LiveObservation observation,
        MapLocalGeometryPose first, MapLocalGeometryPose second) =>
        AutomaticGeometryPosesAgree(observation, new MapOverlayTransform
        {
            ScaleX = first.Scale, ScaleY = first.Scale,
            OffsetX = first.OffsetX, OffsetY = first.OffsetY
        }, second);
}
