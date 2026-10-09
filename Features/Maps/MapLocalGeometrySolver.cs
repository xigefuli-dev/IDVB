using OpenCvSharp;
using IDVBuff.Pipeline;
using MapLocalCorner = IDVBuff.Features.Maps.MapLocalCornerGeometry;

namespace IDVBuff.Features.Maps;

internal sealed record MapLocalGeometryPose(
    double Scale, double OffsetX, double OffsetY, int CornerCount,
    double CornerResidual, double WeightedScore, Vpsg3SpatialResult Spatial);

internal sealed record MapLocalGeometrySearch(
    bool Complete, int QueryCorners, int ReferenceCorners,
    IReadOnlyList<MapLocalGeometryPose> Poses, string FailureReason)
{
    // These are the exact corners used by this floor's search. Retain them for
    // same-frame strict-pose checks instead of extracting the same image again.
    internal IReadOnlyList<MapLocalCorner> ReferenceGeometry { get; init; } = [];
}

/// <summary>
/// Measured corner pairs propose scale without assuming repeated walls.
/// Source entrance annotations constrain location, never determine identity.
/// All compatible pairs are examined; budget exhaustion remains unresolved.
/// </summary>
internal static partial class MapLocalGeometrySolver
{
    private const double CornerMatchTolerance = Vpsg3LiveObservation.AutomaticPoseTolerancePixels;
    private sealed record CornerPairSeed(double Scale, double X, double Y, int Count,
        double Error, int SourceA, int QueryA, int SourceB, int QueryB,
        Point2d PairCenter, double PairLength, Point2d AnchorCenter, double AnchorRadius);

    internal static bool IsGatePoseCompatible(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, IReadOnlyList<NormalizedRectangle> anchors,
        IReadOnlyList<Point2d> liveGates, double scale, double x, double y) =>
        anchors.Any(anchor =>
        {
            var center = new Point2d((anchor.X + anchor.Width / 2) * floor.ReferenceWidth,
                (anchor.Y + anchor.Height / 2) * floor.ReferenceHeight);
            var radius = Math.Max(80, Math.Max(anchor.Width * floor.ReferenceWidth,
                anchor.Height * floor.ReferenceHeight) * 3);
            return liveGates.Any(gate => Distance(gate, center * scale
                + new Point2d(x - observation.ViewportBounds.X, y - observation.ViewportBounds.Y))
                <= Math.Max(8, radius * scale / 6));
        });

    internal static (int Count, double Error) CountPoseMatches(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, IReadOnlyList<MapLocalCorner> query,
        double scale, double offsetX, double offsetY,
        IReadOnlyList<MapLocalCorner>? referenceGeometry = null)
    {
        var source = referenceGeometry;
        if (source is null)
            source = GetPreparedReferenceCorners(floor);
        var queryCells = Enumerable.Range(0, query.Count).GroupBy(i => CornerCell(query[i].Point))
            .ToDictionary(group => group.Key, group => group.ToArray());
        return CountMatches(source, query, queryCells, scale,
            new Point2d(offsetX - observation.ViewportBounds.X, offsetY - observation.ViewportBounds.Y));
    }
    public static MapLocalGeometrySearch Solve(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, IReadOnlyList<MapLocalCorner> query,
        IReadOnlyList<NormalizedRectangle> anchors, IReadOnlyList<Point2d> liveGates,
        CancellationToken cancellationToken)
    {
        try
        {
            return SolveCore(observation, floor, query, anchors, liveGates, cancellationToken);
        }
        catch (GeometryBudgetExceededException)
        {
            return new(false, query.Count, 0, [], "入口几何比较超过时间预算，候选未排除。");
        }
    }

    private static MapLocalGeometrySearch SolveCore(Vpsg3LiveObservation observation,
        Vpsg3PreparedFloor floor, IReadOnlyList<MapLocalCorner> query,
        IReadOnlyList<NormalizedRectangle> anchors, IReadOnlyList<Point2d> liveGates,
        CancellationToken cancellationToken)
    {
        var diagnostics = CornerSearchDiagnostics.Start(query.Count);
        try
        {
            var result = SolveCoreWithDiagnostics(observation, floor, query, anchors,
                liveGates, cancellationToken, diagnostics);
            diagnostics?.RecordReturnedResult(result);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            diagnostics?.RecordThrown("cancelled", exception.GetType().FullName);
            throw;
        }
        catch (GeometryBudgetExceededException exception)
        {
            diagnostics?.RecordThrown("geometry-budget-exceeded", exception.GetType().FullName);
            throw;
        }
        catch (Exception exception)
        {
            diagnostics?.RecordThrown("exception", exception.GetType().FullName);
            throw;
        }
        finally
        {
            diagnostics?.Write();
        }
    }

    private static MapLocalGeometrySearch SolveCoreWithDiagnostics(
        Vpsg3LiveObservation observation, Vpsg3PreparedFloor floor,
        IReadOnlyList<MapLocalCorner> query, IReadOnlyList<NormalizedRectangle> anchors,
        IReadOnlyList<Point2d> liveGates, CancellationToken cancellationToken,
        CornerSearchDiagnostics? diagnostics)
    {
        // Keep one complete, private corner snapshot for proposal generation,
        // pair collection and the result returned to downstream validation.
        query = query.ToArray();
        if (query.Count < 6 || anchors.Count == 0 || liveGates.Count == 0)
            return new(false, query.Count, 0, [], "入口附近的实测墙角或门位置不足。");
        using var sourceExtraction = MapOperationTraceAmbient.StartChild(
            "geometry_reference_corners", MapOperationWaitKind.Compute,
            route: $"query={query.Count}/reference={floor.ReferenceWidth}x{floor.ReferenceHeight}");
        IReadOnlyList<MapLocalCorner> source = GetPreparedReferenceCorners(floor);
        diagnostics?.SetReferenceCornerCount(source.Count);
        sourceExtraction.Complete();
        if (source.Count < 6)
            return new(false, query.Count, source.Count, [], "参考楼层没有足够的可比较墙角。");

        using var pairCollection = MapOperationTraceAmbient.StartChild(
            "geometry_pair_seed_collection", MapOperationWaitKind.Compute,
            route: $"query={query.Count}/source={source.Count}/gates={liveGates.Count}");
        var compatible = new List<int>[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            compatible[i] = [];
            for (var q = 0; q < query.Count; q++)
                if (SameDirections(source[i], query[q])) compatible[i].Add(q);
        }
        var pairSeeds = new List<CornerPairSeed>();
        var cfg = Vpsg3TuningConfig.Default;
        var queryCells = CreateCountMatchQueryCellsCore(source, query, cancellationToken);
        foreach (var anchor in GetReferenceAnchorPairs(floor, source, anchors))
        {
            var center = anchor.Center;
            var radius = anchor.Radius;
            // Every seed corner lies within radius of this reference gate.
            // The supported scale range and gate tolerance therefore give a
            // physical bound on query corners, without truncating hypotheses.
            var queryGateRadius = radius * cfg.MaxSupportedScale * (1 + 1d / 6) + 8;
            var nearCompatible = compatible.Select(indices => indices.Where(q =>
                liveGates.Any(gate => Distance(query[q].Point, gate) <= queryGateRadius))
                .ToArray()).ToArray();
            foreach (var pair in anchor.Pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                    return new(false, query.Count, source.Count, [], "入口几何比较超过时间预算。");
                    var si = pair.CornerOrdinalA;
                    var sj = pair.CornerOrdinalB;
                    var delta = pair.Delta;
                    var denominator = pair.SquaredLength;
                    if (denominator < 14 * 14) continue;
                    foreach (var qi in nearCompatible[si])
                    foreach (var qj in nearCompatible[sj])
                    {
                        CheckGeometryBudget(cancellationToken);
                        if (qi == qj) continue;
                        var qdelta = query[qj].Point - query[qi].Point;
                        var scale = Dot(qdelta, delta) / denominator;
                        if (scale < cfg.MinSupportedScale || scale > cfg.MaxSupportedScale
                            || Distance(qdelta, delta * scale) > 2 * CornerMatchTolerance) continue;
                        // Both measured raster corners can differ by the same
                        // allowed point error. Fit the pair symmetrically so
                        // one corner does not absorb the other's pixel noise.
                        var translation = (query[qi].Point + query[qj].Point
                            - (source[si].Point + source[sj].Point) * scale) * .5;
                        var predictedGate = center * scale + translation;
                        var gateTolerance = Math.Max(8, radius * scale / 6);
                        var pairCenter = (source[si].Point + source[sj].Point) * .5;
                        var pairLength = Math.Sqrt(denominator);
                        var predictionError = CornerMatchTolerance * (1
                            + 2 * Distance(center, pairCenter) / pairLength);
                        if (!liveGates.Any(gate => Distance(gate, predictedGate)
                            <= gateTolerance + predictionError))
                            continue;
                        var (count, residual) = CountMatches(source, query, queryCells, scale, translation);
                        // A pair only proposes a pose. Precision refinement
                        // must run before requiring six independent matches;
                        // a noisy pair can otherwise reject the correct scale.
                        if (count < 2) continue;
                        // Weak pairs retain their individual uncertainty band
                        // for sparse-corner recovery; do not merge that metadata.
                        var seed = new CornerPairSeed(scale, translation.X, translation.Y, count, residual,
                            si, qi, sj, qj, pairCenter, pairLength, center, radius);
                        pairSeeds.Add(seed);
                        diagnostics?.RecordPairSeed();
                    }
            }
        }

        pairCollection.Complete();
        using var proposalRecovery = MapOperationTraceAmbient.StartChild(
            "geometry_pair_proposal_recovery", MapOperationWaitKind.Compute,
            route: $"pairSeeds={pairSeeds.Count}");
        var seeds = new List<(double Scale, double X, double Y, int Count, double Error)>();
        var fitMemo = new CornerFitMemo { SearchDiagnostics = diagnostics };
        void AddSeed((double Scale, double X, double Y, int Count, double Error) fit)
        {
            var duplicate = seeds.FindIndex(seed => SamePose(seed.Scale, seed.X, seed.Y,
                fit.Scale, fit.X, fit.Y, observation));
            if (duplicate < 0)
            {
                seeds.Add(fit);
                diagnostics?.RecordSeedAdded();
            }
            else if (fit.Count > seeds[duplicate].Count
                || (fit.Count == seeds[duplicate].Count && fit.Error < seeds[duplicate].Error))
            {
                seeds[duplicate] = fit;
                diagnostics?.RecordSeedReplaced();
            }
            else diagnostics?.RecordSeedMergedUnchanged();
        }
        var refinementResults = new Dictionary<(double Scale, double X, double Y),
            (double RefinedScale, double RefinedX, double RefinedY, double BestScore, int Probes)>();
        (double RefinedScale, double RefinedX, double RefinedY, double BestScore, int Probes)
            RefineSeed(double scale, double x, double y, bool finalSeedRefinement = false)
        {
            if (finalSeedRefinement) diagnostics?.RecordFinalRefineCall();
            else diagnostics?.RecordRecoveryRefineCall();
            var key = (scale, x, y);
            if (refinementResults.TryGetValue(key, out var cached))
            {
                if (finalSeedRefinement) diagnostics?.RecordFinalRefineCacheHit();
                else diagnostics?.RecordRecoveryRefineCacheHit();
                return cached;
            }
            CheckGeometryBudget(cancellationToken);
            if (finalSeedRefinement) diagnostics?.RecordFinalRefineComputeStart();
            else diagnostics?.RecordRecoveryRefineComputeStart();
            var result = Vpsg3LocalRefiner.Refine(observation.SparseEdgePoints, floor,
                scale, observation.ViewportBounds.X + x, observation.ViewportBounds.Y + y,
                observation.ViewportBounds, observation.Width, observation.Height);
            refinementResults.Add(key, result);
            return result;
        }
        var nativeRecoveredPairs = 0;
        var minimaxPairs = 0;
        foreach (var seed in pairSeeds)
        {
            CheckGeometryBudget(cancellationToken);
            if (seed.Count >= 6)
            {
                if (GatePose(seed.Scale, observation.ViewportBounds.X + seed.X,
                    observation.ViewportBounds.Y + seed.Y))
                    AddSeed((seed.Scale, seed.X, seed.Y, seed.Count, seed.Error));
                continue;
            }
            // Each pair is a finite precision proposal, like the normal VPSG
            // route. Try its actual joint refinement before the sparse-corner
            // recovery. A recovered six-corner proposal needs no C(N,6) fit.
            var native = RefineSeed(seed.Scale, seed.X, seed.Y);
            var nativeMatched = CountMatches(source, query, queryCells, native.RefinedScale,
                new Point2d(native.RefinedX - observation.ViewportBounds.X,
                    native.RefinedY - observation.ViewportBounds.Y));
            var nativeSpatial = nativeMatched.Count >= 6
                ? Vpsg3VerificationGate.EvaluateSpatialVerification(observation.SparseEdgePoints,
                    observation.ValidMask, floor, native.RefinedScale, native.RefinedX,
                    native.RefinedY, observation.ViewportBounds, observation.Width, observation.Height)
                : default;
            if (nativeMatched.Count >= 6 && native.BestScore >= cfg.MinVerificationScore
                && nativeSpatial.IsSpatiallyConsistent
                && GatePose(native.RefinedScale, native.RefinedX, native.RefinedY))
            {
                nativeRecoveredPairs++;
                var fit = (Scale: native.RefinedScale, X: native.RefinedX - observation.ViewportBounds.X,
                    Y: native.RefinedY - observation.ViewportBounds.Y,
                    Count: nativeMatched.Count, Error: nativeMatched.Error);
                AddSeed(fit);
                refinementResults[(fit.Scale, fit.X, fit.Y)] = native;
                continue;
            }
            minimaxPairs++;
            fitMemo.SetGeneratingGateDiagnosticContext(seed.SourceA, seed.QueryA,
                seed.SourceB, seed.QueryB, seed.AnchorCenter, seed.AnchorRadius, liveGates);
            try
            {
                foreach (var fit in FitMeasuredCornerProposals(source, query, queryCells,
                    seed.Scale, new Point2d(seed.X, seed.Y), seed.PairCenter, seed.PairLength,
                    seed.SourceA, seed.QueryA, seed.SourceB, seed.QueryB,
                    fitMemo, cancellationToken))
                {
                    var generatingGatePass = IsGeneratingGateCompatible(seed.AnchorCenter,
                        seed.AnchorRadius, liveGates, fit.Scale, fit.X, fit.Y);
                    diagnostics?.RecordGeneratingGateResult(generatingGatePass);
                    if (generatingGatePass) AddSeed(fit);
                }
            }
            finally
            {
                fitMemo.ClearGeneratingGateDiagnosticContext();
            }
            // The normal joint refiner can also recover a noisy pair. Only
            // its final six-corner result may enter the returned pose pool.
            AddSeed((seed.Scale, seed.X, seed.Y, seed.Count, seed.Error));
        }
        proposalRecovery.Complete();
        diagnostics?.RecordRecoveryComplete();
        MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration, MapLogLevel.Info,
            "入口几何提案精修完成", details: new()
            {
                ["referenceWidth"] = floor.ReferenceWidth,
                ["referenceHeight"] = floor.ReferenceHeight,
                ["pairCount"] = pairSeeds.Count,
                ["nativeRecoveredPairs"] = nativeRecoveredPairs,
                ["minimaxPairs"] = minimaxPairs,
                ["minimaxSixGroups"] = fitMemo.SixMatches.Count,
                ["measuredTriplets"] = fitMemo.Triplets.Count,
                ["poseSeedCount"] = seeds.Count
            });

        var poses = new List<MapLocalGeometryPose>();
        void AddPose(MapLocalGeometryPose pose)
        {
            var duplicate = poses.FindIndex(other => SamePose(other.Scale,
                other.OffsetX - observation.ViewportBounds.X, other.OffsetY - observation.ViewportBounds.Y,
                pose.Scale, pose.OffsetX - observation.ViewportBounds.X,
                pose.OffsetY - observation.ViewportBounds.Y, observation));
            if (duplicate < 0)
            {
                poses.Add(pose);
                diagnostics?.RecordPoseAdded(poses.Count);
            }
            else if (pose.WeightedScore > poses[duplicate].WeightedScore)
            {
                poses[duplicate] = pose;
                diagnostics?.RecordPoseReplaced(poses.Count);
            }
            else diagnostics?.RecordPoseMergedUnchanged(poses.Count);
        }
        bool GatePose(double scale, double x, double y) => IsGatePoseCompatible(
            observation, floor, anchors, liveGates, scale, x, y);
        foreach (var seed in seeds)
        {
            diagnostics?.RecordEnteredFinalSeed();
            cancellationToken.ThrowIfCancellationRequested();
            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
                return new(false, query.Count, source.Count, [], "入口几何精修超过时间预算。");
            if (seed.Count >= 6 && GatePose(seed.Scale,
                observation.ViewportBounds.X + seed.X, observation.ViewportBounds.Y + seed.Y))
            {
                // Refinement may find another valid basin; it cannot disprove
                // the measured pose that generated this candidate.
                var originalSpatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
                    observation.SparseEdgePoints, observation.ValidMask, floor,
                    seed.Scale, observation.ViewportBounds.X + seed.X,
                    observation.ViewportBounds.Y + seed.Y, observation.ViewportBounds,
                    observation.Width, observation.Height);
                AddPose(new(seed.Scale, observation.ViewportBounds.X + seed.X,
                    observation.ViewportBounds.Y + seed.Y, seed.Count, seed.Error,
                    Vpsg3LocalRefiner.EvaluateScore(observation.SparseEdgePoints, floor,
                        seed.Scale, observation.ViewportBounds.X + seed.X,
                        observation.ViewportBounds.Y + seed.Y, observation.ViewportBounds), originalSpatial));
            }
            var refined = RefineSeed(seed.Scale, seed.X, seed.Y, finalSeedRefinement: true);
            var matched = CountMatches(source, query, queryCells, refined.RefinedScale,
                new Point2d(refined.RefinedX - observation.ViewportBounds.X,
                    refined.RefinedY - observation.ViewportBounds.Y));
            if (matched.Count < 6)
            {
                if (seed.Count < 6) continue;
                // Photometric refinement cannot replace the measured-corner
                // evidence that supplied this independent scale proposal.
                refined = (seed.Scale, observation.ViewportBounds.X + seed.X,
                    observation.ViewportBounds.Y + seed.Y,
                    Vpsg3LocalRefiner.EvaluateScore(observation.SparseEdgePoints,
                        floor, seed.Scale, observation.ViewportBounds.X + seed.X,
                        observation.ViewportBounds.Y + seed.Y, observation.ViewportBounds), 0);
                matched = (seed.Count, seed.Error);
            }
            var spatial = Vpsg3VerificationGate.EvaluateSpatialVerification(
                observation.SparseEdgePoints, observation.ValidMask, floor,
                refined.RefinedScale, refined.RefinedX, refined.RefinedY,
                observation.ViewportBounds, observation.Width, observation.Height);
            if (!GatePose(refined.RefinedScale, refined.RefinedX, refined.RefinedY)) continue;
            var pose = new MapLocalGeometryPose(refined.RefinedScale, refined.RefinedX,
                refined.RefinedY, matched.Count, matched.Error, refined.BestScore, spatial);
            AddPose(pose);
        }
        diagnostics?.RecordFinalIterationComplete();
        return new(!fitMemo.HasUncertifiedFits, query.Count, source.Count,
            poses.OrderByDescending(pose => pose.WeightedScore).ToArray(),
            fitMemo.HasUncertifiedFits ? "部分入口墙角拟合仍无法排除，候选未完成比较。" : string.Empty)
            { ReferenceGeometry = source };
    }

    private static (int Count, double Error) CountMatches(IReadOnlyList<MapLocalCorner> source,
        IReadOnlyList<MapLocalCorner> query, Dictionary<(int X, int Y), int[]> queryCells,
        double scale, Point2d translation)
    {
        var pairs = TryCollectCountPairs(source, query, queryCells, scale, translation);
        var queryNeighbors = pairs is not null ? TryGetCountQueryNeighbors(source, query, queryCells) : null;
        if (pairs is null)
        {
            pairs = new List<(int Source, int Query, double Error)>();
            for (var si = 0; si < source.Count; si++)
            {
                var projected = source[si].Point * scale + translation;
                var cell = CornerCell(projected);
                for (var cy = cell.Y - 1; cy <= cell.Y + 1; cy++)
                for (var cx = cell.X - 1; cx <= cell.X + 1; cx++)
                {
                    if (!queryCells.TryGetValue((cx, cy), out var indices)) continue;
                    foreach (var qi in indices)
                    {
                        var error = Distance(projected, query[qi].Point);
                        if (error <= CornerMatchTolerance && SameDirections(source[si], query[qi]))
                            pairs.Add((si, qi, error));
                    }
                }
            }
        }
        var usedSource = new HashSet<int>();
        var usedQuery = new HashSet<int>();
        double residual = 0;
        foreach (var pair in pairs.OrderBy(pair => pair.Error))
        {
            if (usedSource.Contains(pair.Source) || usedQuery.Contains(pair.Query)) continue;
            // The inner and outer contours of one raster wall describe the
            // same physical turn; they cannot count as independent evidence.
            if (usedQuery.Any(index => queryNeighbors is not null ? queryNeighbors[index][pair.Query]
                    : Distance(query[index].Point, query[pair.Query].Point) < 6)
                || usedSource.Any(index => Distance(source[index].Point,
                    source[pair.Source].Point) * scale < 6)) continue;
            usedSource.Add(pair.Source);
            usedQuery.Add(pair.Query);
            residual += pair.Error;
        }
        return (usedQuery.Count, usedQuery.Count == 0 ? double.PositiveInfinity : residual / usedQuery.Count);
    }

    private static bool SameDirections(MapLocalCorner a, MapLocalCorner b) =>
        Dot(a.RayA, b.RayA) >= .95 && Dot(a.RayB, b.RayB) >= .95;

    private static bool SamePose(double s1, double x1, double y1,
        double s2, double x2, double y2, Vpsg3LiveObservation observation)
    {
        // Compare inverse projection at both ends of the observation; two
        // different scales sharing the entrance are not the same pose.
        var a = new Point2d((0 - x1) / s1, (0 - y1) / s1);
        var b = new Point2d((0 - x2) / s2, (0 - y2) / s2);
        var c = new Point2d((observation.Width - x1) / s1, (observation.Height - y1) / s1);
        var d = new Point2d((observation.Width - x2) / s2, (observation.Height - y2) / s2);
        return Distance(a, b) * Math.Max(s1, s2) < 3
            && Distance(c, d) * Math.Max(s1, s2) < 3;
    }

    private static double Dot(Point2d a, Point2d b) => a.X * b.X + a.Y * b.Y;
    private sealed class GeometryBudgetExceededException : Exception { }
    private static void CheckGeometryBudget(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
            throw new GeometryBudgetExceededException();
    }
    private static (int X, int Y) CornerCell(Point2d point) =>
        ((int)Math.Floor(point.X / 8), (int)Math.Floor(point.Y / 8));
    private static double Distance(Point2d a, Point2d b) => double.Hypot(a.X - b.X, a.Y - b.Y);
}
