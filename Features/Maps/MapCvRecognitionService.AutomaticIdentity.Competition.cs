using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    // Compare observed map-specific walls, not the fringe of a shared wall
    // displaced by pose rounding. Unknown pixels never prove free space.
    private static List<AutomaticIdentityFloorWork> RemoveDominatedAutomaticIdentities(
        Vpsg3LiveObservation observation,
        List<AutomaticIdentityFloorWork> verified,
        double minimumWallSpanPixels,
        CancellationToken cancellationToken,
        bool identityGroups = false,
        HashSet<AutomaticIdentityFloorWork>? admittedWitnesses = null)
    {
        if (verified.Count < 2)
            return verified;

        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return verified;
        using var nonzero = new Mat();
        Cv2.FindNonZero(observation.ObservedEdges, nonzero);
        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return verified;
        var pointCount = nonzero.Rows;
        var points = new List<Point>(pointCount);
        for (var index = 0; index < pointCount; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AutomaticIdentityCompetitionBudgetExhausted())
                    return verified;
            }
            var point = nonzero.At<Point>(index, 0);
            if (observation.ValidMask.At<byte>(point.Y, point.X) >= 128)
                points.Add(point);
        }

        var supports = new List<bool[]>(verified.Count);
        var floorCaches = new List<AutomaticIdentityCompetitionFloorCache>();
        var poseCaches = new List<AutomaticIdentityCompetitionPoseCache>(verified.Count);
        foreach (var candidate in verified)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AutomaticIdentityCompetitionBudgetExhausted())
                return verified; // Caller reports TimedOut before any publication.
            var transform = candidate.AlignmentAttempt?.Recognition?.Result.OverlayTransform;
            if (transform is null || candidate.Lease is null
                || !double.IsFinite(transform.ScaleX) || transform.ScaleX <= 0
                || !double.IsFinite(transform.ScaleY) || transform.ScaleY <= 0
                || !double.IsFinite(transform.OffsetX) || !double.IsFinite(transform.OffsetY)
                || transform.OrientationDegrees != 0
                || candidate.Lease.Floor.ReferenceEdgePoints.IsEmpty)
            {
                // A transform outside this comparator's coordinate model is
                // unresolved; preserve every identity instead of excluding it.
                return verified;
            }

            var floor = candidate.Lease.Floor;
            var floorCache = floorCaches.FirstOrDefault(existing =>
                ReferenceEquals(existing.Floor, floor));
            if (floorCache is null)
            {
                var exactEdgeBitmap = BuildAutomaticIdentityExactEdgeBitmap(
                    floor, cancellationToken);
                if (exactEdgeBitmap is null)
                    return verified;
                floorCache = new(floor, exactEdgeBitmap);
                floorCaches.Add(floorCache);
            }

            var poseCache = BuildAutomaticIdentityPoseCache(
                observation, points, floorCache, transform, cancellationToken);
            if (poseCache is null)
                return verified;

            var support = new bool[points.Count];
            var hits = 0;
            for (var index = 0; index < points.Count; index++)
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AutomaticIdentityCompetitionBudgetExhausted())
                        return verified;
                }
                var point = points[index];
                var referencePoint = poseCache.ObservedReferencePoints[index];
                support[index] = floor.IsHitK5(referencePoint.X, referencePoint.Y);
                if (support[index])
                    hits++;
            }
            poseCache.ReleaseObservedReferencePoints();
            candidate.SupportedObservedPointCount = hits;
            supports.Add(support);
            poseCaches.Add(poseCache);
        }

        var dominates = new bool[verified.Count, verified.Count];
        var compared = new bool[verified.Count, verified.Count];
        var pairs = new List<object>();
        var budgetExhausted = false;
        bool Dominates(int index, int other)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AutomaticIdentityCompetitionBudgetExhausted())
            {
                budgetExhausted = true;
                return false;
            }
            if (!compared[index, other])
            {
                var first = CountDistinctiveAutomaticIdentityEvidence(
                    points, poseCaches[index], supports[index],
                    poseCaches[other], supports[other], minimumWallSpanPixels,
                    cancellationToken);
                if (first is null || AutomaticIdentityCompetitionBudgetExhausted())
                {
                    budgetExhausted = true;
                    return false;
                }
                var second = CountDistinctiveAutomaticIdentityEvidence(
                    points, poseCaches[other], supports[other],
                    poseCaches[index], supports[index], minimumWallSpanPixels,
                    cancellationToken);
                if (second is null || AutomaticIdentityCompetitionBudgetExhausted())
                {
                    budgetExhausted = true;
                    return false;
                }
                dominates[index, other] = first.Value.UsableObservedPoints > 0
                    && second.Value.UsableObservedPoints == 0;
                dominates[other, index] = second.Value.UsableObservedPoints > 0
                    && first.Value.UsableObservedPoints == 0;
                compared[index, other] = compared[other, index] = true;
                pairs.Add(new
                {
                    FirstPoseIndex = index,
                    FirstMapId = verified[index].Map.Id,
                    FirstFloor = verified[index].FloorKey,
                    FirstDistinctiveObservedPoints = first.Value.ObservedPoints,
                    FirstGeometricObservedPoints = first.Value.UsableObservedPoints,
                    FirstUsableWallComponents = first.Value.UsableComponents,
                    SecondPoseIndex = other,
                    SecondMapId = verified[other].Map.Id,
                    SecondFloor = verified[other].FloorKey,
                    SecondDistinctiveObservedPoints = second.Value.ObservedPoints,
                    SecondGeometricObservedPoints = second.Value.UsableObservedPoints,
                    SecondUsableWallComponents = second.Value.UsableComponents
                });
            }
            return dominates[index, other];
        }

        var groups = new List<List<int>>();
        for (var index = 0; index < verified.Count; index++)
        {
            var group = identityGroups ? groups.FirstOrDefault(existing =>
                SameAutomaticIdentity(verified[existing[0]], verified[index])) : null;
            if (group is null) groups.Add([index]);
            else group.Add(index);
        }
        var beatenPose = new bool[verified.Count];
        for (var pose = 0; pose < verified.Count; pose++)
        for (var other = 0; other < verified.Count; other++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AutomaticIdentityCompetitionBudgetExhausted() || budgetExhausted)
                return verified;
            if (pose == other || (identityGroups
                && SameAutomaticIdentity(verified[pose], verified[other]))) continue;
            // Complete the known bidirectional graph before removing anything.
            // A different pose of this same identity cannot rescue a beaten
            // pose and let it act as a supposedly undefeated witness.
            beatenPose[pose] |= Dominates(other, pose);
            if (budgetExhausted)
                return verified;
        }
        var witnesses = new int[groups.Count, groups.Count];
        for (var source = 0; source < groups.Count; source++)
        for (var target = 0; target < groups.Count; target++)
        {
            if (AutomaticIdentityCompetitionBudgetExhausted() || budgetExhausted)
                return verified;
            witnesses[source, target] = -1;
            if (source == target) continue;
            foreach (var pose in groups[source])
            {
                if (AutomaticIdentityCompetitionBudgetExhausted() || budgetExhausted)
                    return verified;
                if (beatenPose[pose]
                    || (admittedWitnesses is not null && !admittedWitnesses.Contains(verified[pose])))
                    continue;
                // A known alternative is part of the target's identity. The
                // same observed source pose must defeat ALL of its checked poses.
                var defeatsEveryTargetPose = true;
                foreach (var other in groups[target])
                {
                    if (!Dominates(pose, other))
                    {
                        defeatsEveryTargetPose = false;
                        break;
                    }
                    if (budgetExhausted)
                        return verified;
                }
                if (budgetExhausted)
                    return verified;
                if (!defeatsEveryTargetPose)
                    continue;
                witnesses[source, target] = pose;
                break;
            }
        }
        // Only an undefeated identity supplies an exclusion witness. Do not
        // transitively delete a chain, or delete a cycle to manufacture one
        // survivor. Identical undefeated identities may remove weak rivals
        // while both remain ambiguous. Unknown work is guarded by the caller.
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return verified;
        var removed = new HashSet<int>();
        var dominanceWitnessByPose = new int[verified.Count];
        Array.Fill(dominanceWitnessByPose, -1);
        for (var target = 0; target < groups.Count; target++)
        for (var source = 0; source < groups.Count; source++)
        {
            if (AutomaticIdentityCompetitionBudgetExhausted())
                return verified;
            var witness = witnesses[source, target];
            if (witness < 0) continue;
            foreach (var index in groups[target])
            {
                removed.Add(index);
                dominanceWitnessByPose[index] = witness;
            }
            break;
        }
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return verified;
        var remaining = verified.Where((_, index) => !removed.Contains(index)).ToList();

        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "Automatic identity observed-structure competition", details: new()
            {
                ["observedPointCount"] = points.Count,
                ["minimumDistinctiveWallSpanReferencePixels"] = minimumWallSpanPixels,
                ["remainingIdentityCount"] = remaining.Select(candidate =>
                    (candidate.Map.Id, candidate.FloorKey)).Distinct().Count(),
                ["remainingPoseCount"] = remaining.Count,
                ["pairEvidence"] = pairs,
                ["candidateSupport"] = verified.Select((candidate, poseIndex) =>
                {
                    var witness = dominanceWitnessByPose[poseIndex];
                    return new
                    {
                        PoseIndex = poseIndex,
                        candidate.Map.Id,
                        candidate.Map.SequenceNumber,
                        candidate.FloorKey,
                        candidate.SupportedObservedPointCount,
                        DominatedByMapId = witness >= 0
                            ? verified[witness].Map.Id : candidate.DominatedByMapId,
                        DominatedByFloorKey = witness >= 0
                            ? verified[witness].FloorKey : candidate.DominatedByFloorKey,
                        Transform = candidate.AlignmentAttempt?.Recognition?.Result.OverlayTransform
                    };
                }).ToArray()
            });
        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return verified;
        for (var index = 0; index < verified.Count; index++)
        {
            var witness = dominanceWitnessByPose[index];
            if (witness < 0)
                continue;
            var candidate = verified[index];
            candidate.DominatedByMapId = verified[witness].Map.Id;
            candidate.DominatedByFloorKey = verified[witness].FloorKey;
            candidate.Status = MapAutomaticIdentityCandidateStatus.DominatedByObservedStructure;
            candidate.FailureReason = "另一候选解释了已观察到的独有墙段，当前候选没有达到跨度要求的反向独有墙段证据。";
        }
        return AutomaticIdentityCompetitionBudgetExhausted() ? verified : remaining;
    }

    private static bool SameAutomaticIdentity(AutomaticIdentityFloorWork first,
        AutomaticIdentityFloorWork second) => first.Map.Id == second.Map.Id
        && string.Equals(first.FloorKey, second.FloorKey, StringComparison.OrdinalIgnoreCase);

    private static List<AutomaticIdentityPoseEvidence> RemoveDominatedAutomaticPoseIdentities(
        Vpsg3LiveObservation observation, List<AutomaticIdentityPoseEvidence> evidence,
        double minimumWallSpanPixels, CancellationToken cancellationToken,
        AutomaticGeometryInput input)
    {
        evidence = ExcludeContradictedAutomaticPoses(observation, evidence,
            minimumWallSpanPixels, input, cancellationToken);
        // Each strict final pose is a separate rival. A map may be excluded
        // only when one observed pose directly defeats every rival pose of it;
        // an earlier winning pose cannot erase a later alternative of that map.
        var nodes = evidence.Select(pose => new AutomaticIdentityFloorWork(
            new(pose.Owner.Map, pose.Owner.FloorKey, true, string.Empty))
        {
            Lease = pose.Owner.Lease,
            AlignmentAttempt = pose.Attempt
        }).ToList();
        var remaining = RemoveDominatedAutomaticIdentities(observation, nodes,
            minimumWallSpanPixels, cancellationToken, identityGroups: true,
            admittedWitnesses: nodes.Where((_, index) => evidence[index].CanAccept).ToHashSet())
            .ToHashSet();
        return evidence.Where((_, index) => remaining.Contains(nodes[index])).ToList();
    }

    private readonly record struct AutomaticIdentityDistinctiveEvidence(
        int ObservedPoints, int UsableObservedPoints, int UsableComponents);

    private static AutomaticIdentityDistinctiveEvidence? CountDistinctiveAutomaticIdentityEvidence(
        IReadOnlyList<Point> points,
        AutomaticIdentityCompetitionPoseCache source,
        bool[] sourceSupport,
        AutomaticIdentityCompetitionPoseCache other,
        bool[] otherSupport,
        double minimumWallSpanPixels,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return null;

        var sourceFloor = source.Floor;
        var otherFloor = other.Floor;
        var width = sourceFloor.ReferenceWidth;
        var sourceTransform = source.Transform;
        var otherTransform = other.Transform;
        var otherInverseX = other.InverseScaleX;
        var otherInverseY = other.InverseScaleY;
        var otherWallHitBySourceIndex = new Dictionary<int, bool>(
            Math.Min(source.NeighborhoodWallIndices.Length, 1024));
        var distinctivePoints = new List<Point>();
        var minimumX = int.MaxValue;
        var minimumY = int.MaxValue;
        var maximumX = int.MinValue;
        var maximumY = int.MinValue;
        var neighborhoodChecks = 0;
        for (var index = 0; index < points.Count; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AutomaticIdentityCompetitionBudgetExhausted())
                    return null;
            }
            if (!sourceSupport[index] || otherSupport[index])
                continue;

            var point = points[index];
            var hit = false;
            var firstWall = source.NeighborhoodOffsets[index];
            var afterLastWall = source.NeighborhoodOffsets[index + 1];
            for (var wallIndex = firstWall; wallIndex < afterLastWall; wallIndex++)
            {
                if ((neighborhoodChecks++ & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AutomaticIdentityCompetitionBudgetExhausted())
                        return null;
                }

                var sourceWallIndex = source.NeighborhoodWallIndices[wallIndex];
                if (!otherWallHitBySourceIndex.TryGetValue(sourceWallIndex,
                    out var isHitByOther))
                {
                    var wallX = sourceWallIndex % width;
                    var wallY = sourceWallIndex / width;
                    // Keep the original operation order and Math.Round default
                    // (to-even); algebraic scale-ratio rewrites change edge cases.
                    var otherX = (int)Math.Round((wallX * sourceTransform.ScaleX
                        + sourceTransform.OffsetX - otherTransform.OffsetX) * otherInverseX);
                    var otherY = (int)Math.Round((wallY * sourceTransform.ScaleY
                        + sourceTransform.OffsetY - otherTransform.OffsetY) * otherInverseY);
                    isHitByOther = otherFloor.IsHitK5(otherX, otherY);
                    otherWallHitBySourceIndex.Add(sourceWallIndex, isHitByOther);
                }
                if (!isHitByOther)
                {
                    hit = true;
                    break;
                }
            }

            if (hit)
            {
                distinctivePoints.Add(point);
                minimumX = Math.Min(minimumX, point.X);
                minimumY = Math.Min(minimumY, point.Y);
                maximumX = Math.Max(maximumX, point.X);
                maximumY = Math.Max(maximumY, point.Y);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return null;
        if (distinctivePoints.Count == 0)
            return new(0, 0, 0);

        using var observedDistinctiveWalls = new Mat(maximumY - minimumY + 1,
            maximumX - minimumX + 1, MatType.CV_8UC1, Scalar.All(0));
        for (var index = 0; index < distinctivePoints.Count; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AutomaticIdentityCompetitionBudgetExhausted())
                    return null;
            }
            var point = distinctivePoints[index];
            observedDistinctiveWalls.Set<byte>(point.Y - minimumY, point.X - minimumX, 255);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return null;
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var componentCount = Cv2.ConnectedComponentsWithStats(
            observedDistinctiveWalls, labels, stats, centroids,
            PixelConnectivity.Connectivity8);
        cancellationToken.ThrowIfCancellationRequested();
        if (AutomaticIdentityCompetitionBudgetExhausted())
            return null;
        var usablePoints = 0;
        var usableComponents = 0;
        for (var label = 1; label < componentCount; label++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (AutomaticIdentityCompetitionBudgetExhausted())
                return null;
            // Pixel coordinates span N - 1 intervals. Counting N pixel cells
            // here can certify a short texture component as a measured wall.
            var spanX = (stats.At<int>(label, (int)ConnectedComponentsTypes.Width) - 1)
                * source.InverseScaleX;
            var spanY = (stats.At<int>(label, (int)ConnectedComponentsTypes.Height) - 1)
                * source.InverseScaleY;
            // A connected L-shaped wall has real extent along both axes. Its
            // measured endpoints can exceed the required physical span even
            // when neither axis alone does. Never substitute empty bbox corners.
            if (Math.Max(spanX, spanY) < minimumWallSpanPixels)
            {
                var hasMeasuredSpan = HasMeasuredComponentSpan(labels, stats, label,
                    source.InverseScaleX, source.InverseScaleY,
                    minimumWallSpanPixels, cancellationToken);
                if (AutomaticIdentityCompetitionBudgetExhausted())
                    return null;
                if (!hasMeasuredSpan)
                    continue;
            }
            usableComponents++;
            usablePoints += stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return AutomaticIdentityCompetitionBudgetExhausted()
            ? null
            : new(distinctivePoints.Count, usablePoints, usableComponents);
    }

    private static bool HasMeasuredComponentSpan(Mat labels, Mat stats, int label,
        double inverseX, double inverseY, double minimumSpan, CancellationToken cancellationToken)
    {
        var x = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
        var y = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
        var width = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
        var height = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
        if (double.Hypot((width - 1) * inverseX, (height - 1) * inverseY) < minimumSpan)
            return false;
        var points = new List<Point>();
        for (var row = y; row < y + height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = x; column < x + width; column++)
                if (labels.At<int>(row, column) == label) points.Add(new(column, row));
        }
        var hull = Cv2.ConvexHull(points);
        for (var first = 0; first < hull.Length; first++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var second = first + 1; second < hull.Length; second++)
                if (double.Hypot((hull[first].X - hull[second].X) * inverseX,
                    (hull[first].Y - hull[second].Y) * inverseY) >= minimumSpan)
                    return true;
        }
        return false;
    }
}
