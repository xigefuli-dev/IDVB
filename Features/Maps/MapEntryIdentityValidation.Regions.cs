using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record MapEntryRegionalCell(int X, int Y, int Count, double Median, double WithinThree, double WithinSix);
internal sealed record MapEntryRegionalEvidence(bool Verified, string Reason, int MaskEdges,
    int ContrastSupportedEdges, IReadOnlyList<MapEntryRegionalCell> Regions);
internal sealed record MapEntryWallRefinementEvidence(bool Success, bool Accepted, MapEntryIdentityPose Pose,
    string Reason, MapEntryIdentityHeldOut? HeldOut, int SupportCount, int HoldOutCount,
    MapEntryRegionalEvidence? Regions = null, bool? KnownWallRaster = null);
internal sealed record MapEntryPartialWallEvidence(bool Success, MapEntryIdentityPose Pose, string Reason,
    MapEntryRegionalEvidence InitialRegions, MapEntryRegionalEvidence Regions,
    MapEntryIdentityHeldOut? HeldOutNormals, MapEntryIdentityHeldOut? CandidateNormals,
    MapEntryWallRefinementEvidence? ScaleRefinement, MapEntryWallRefinementEvidence? TranslationRefinement);

internal static partial class MapEntryIdentityValidation
{
    private sealed record PartialPlane(int Axis, int Sign, double QueryNormal, double SourceNormal,
        double SourceLo, double SourceHi)
    {
        public double Delta => QueryNormal - SourceNormal;
    }
    private sealed class RegionBudgetExpiredException : Exception { }
    private static void RegionBudget(Func<bool> canCompute)
    {
        if (!canCompute()) throw new RegionBudgetExpiredException();
    }

    /// <summary>9491 regional_validation: every populated 3x3 cell must pass, in current pixels.</summary>
    public static MapEntryRegionalEvidence VerifyRegions(MapEntryIdentityFrame frame, Mat clean, Mat unknown,
        MapEntryIdentityPose pose, Func<bool> canCompute)
    {
        var cells = new List<MapEntryRegionalCell>();
        var maskEdges = 0;
        var observedEdges = 0;
        try
        {
            RegionBudget(canCompute);
            using var queryEdge = new Mat();
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.MorphologyEx(frame.Query, queryEdge, MorphTypes.Gradient, kernel);
            using var positions = queryEdge.FindNonZero();
            maskEdges = (int)positions.Total();
            if (maskEdges == 0) return new(false, "regional-no-query-edges", 0, 0, []);
            var bounds = Cv2.BoundingRect(positions);
            using var projected = Project(clean, pose, frame.Query.Size());
            using var sourceEdge = new Mat();
            Cv2.MorphologyEx(projected, sourceEdge, MorphTypes.Gradient, kernel);
            using var distances = Distances(sourceEdge);
            using var projectedUnknown = Project(unknown, pose, frame.Query.Size());
            using var observed = frame.Observed.Clone();
            observed.SetTo(Scalar.Black, projectedUnknown);
            observedEdges = Cv2.CountNonZero(observed);
            var minimumCount = Math.Max(30, (int)(maskEdges * .015));
            for (var gy = 0; gy < 3; gy++)
            for (var gx = 0; gx < 3; gx++)
            {
                RegionBudget(canCompute);
                var left = (int)Math.Round(bounds.Left + bounds.Width * gx / 3d);
                var right = (int)Math.Round(bounds.Left + bounds.Width * (gx + 1) / 3d);
                var top = (int)Math.Round(bounds.Top + bounds.Height * gy / 3d);
                var bottom = (int)Math.Round(bounds.Top + bounds.Height * (gy + 1) / 3d);
                if (right <= left || bottom <= top) continue;
                var region = new Rect(left, top, right - left, bottom - top);
                using var measured = new Mat(observed, region);
                if (Cv2.CountNonZero(measured) < minimumCount) continue;
                using var field = new Mat(distances, region);
                var values = AtMask(field, measured);
                var residual = Residual(values);
                cells.Add(new(gx, gy, values.Length, residual.Median, residual.Three, residual.Six));
            }
            RegionBudget(canCompute);
            var verified = cells.Count > 0 && cells.All(c => c.Median <= 4.5 && c.WithinThree >= .45 && c.WithinSix >= .62);
            return new(verified, verified ? "regional-current-frame-verified" : "regional-current-frame-failed",
                maskEdges, observedEdges, cells.ToArray());
        }
        catch (RegionBudgetExpiredException)
        {
            return new(false, "regional-budget-incomplete", maskEdges, observedEdges, cells.ToArray());
        }
    }

    /// <summary>
    /// Normal partial_wall_geometry path at 9491, including disjoint spatial holdouts and native scale fit.
    /// It intentionally excludes the committed-pose _fit_only speed mode. Actual-author validation is separate.
    /// </summary>
    public static MapEntryPartialWallEvidence ValidateAndRefine(MapEntryIdentityFrame frame,
        Mat clean, Mat knownBoundary, Mat unknown, IReadOnlyList<MapEntryIdentityWall> nativeWalls,
        MapEntryIdentityPose seed, Func<bool> canCompute,
        bool allowScaleRefinement = true, bool requireInitialRegions = true)
    {
        var initial = VerifyRegions(frame, clean, unknown, seed, canCompute);
        var regions = initial;
        var pose = seed;
        MapEntryIdentityHeldOut? held = null, absolute = null;
        MapEntryWallRefinementEvidence? scale = null, translation = null;
        MapEntryPartialWallEvidence Finish(bool success, string reason) =>
            new(success, pose, reason, initial, regions, held, absolute, scale, translation);
        if (requireInitialRegions && !initial.Verified) return Finish(false, initial.Reason);
        if (nativeWalls.Count == 0) return Finish(false, "partial-native-wall-lines-unavailable");
        if (!ValidPartialPose(seed)) return Finish(false, "partial-invalid-axis-pose");
        try
        {
            RegionBudget(canCompute);
            var projectedWalls = nativeWalls.Select(w => ProjectWall(w, seed)).ToArray();
            var groups = new List<PartialPlane>();
            foreach (var query in frame.Walls)
            {
                RegionBudget(canCompute);
                MapEntryIdentityWall? best = null;
                var bestDistance = double.PositiveInfinity;
                for (var i = 0; i < projectedWalls.Length; i++)
                {
                    if ((i & 127) == 0) RegionBudget(canCompute);
                    var source = projectedWalls[i];
                    var distance = Math.Abs(query.Normal - source.Normal);
                    var overlap = Math.Min(query.Hi, source.Hi) - Math.Max(query.Lo, source.Lo) + 1;
                    if (source.Axis != query.Axis || source.Sign != query.Sign || distance > 48 ||
                        overlap < Math.Min(12, Math.Min(query.Hi - query.Lo - 1, source.Hi - source.Lo - 1))) continue;
                    if (distance < bestDistance) { best = source; bestDistance = distance; }
                }
                if (best is null || groups.Any(g => g.Axis == query.Axis && g.Sign == query.Sign &&
                        Math.Abs(g.QueryNormal - query.Normal) <= 3)) continue;
                groups.Add(new(query.Axis, query.Sign, query.Normal, best.Normal, best.Lo, best.Hi));
            }
            var leaveOneOut = new List<double>();
            for (var axis = 0; axis < 2; axis++)
            {
                var planes = groups.Where(g => g.Axis == axis).ToArray();
                if (planes.Length < 2 || Spread(planes) < 24)
                    return Finish(false, "partial-wall-independent-planes-insufficient");
                for (var i = 0; i < planes.Length; i++)
                    leaveOneOut.Add(Math.Abs(planes[i].Delta - MapEntryIdentityFrame.Median(
                        planes.Where((_, j) => j != i).Select(p => p.Delta))));
            }
            held = Summarize(leaveOneOut);
            absolute = Summarize(groups.Select(g => Math.Abs(g.Delta)));
            if (!WithinWallLimits(held)) return Finish(false, "partial-wall-held-out-failed");
            if (!WithinWallLimits(absolute)) return Finish(false, "partial-wall-candidate-failed");

            var scaleAccepted = false;
            var rasterVerified = false;
            if (allowScaleRefinement)
            {
                scale = FitNativeScale(groups, projectedWalls, seed, canCompute);
                if (scale.Success)
                {
                    var candidateRegions = VerifyRegions(frame, clean, unknown, scale.Pose, canCompute);
                    var raster = VerifyWallRaster(frame, clean, knownBoundary, unknown, scale.Pose, canCompute);
                    scaleAccepted = candidateRegions.Verified && raster && canCompute();
                    scale = scale with { Accepted = scaleAccepted, Regions = candidateRegions, KnownWallRaster = raster };
                    if (scaleAccepted) { pose = scale.Pose; regions = candidateRegions; rasterVerified = true; }
                }
            }
            if (!scaleAccepted)
            {
                translation = FitTranslation(groups, seed);
                if (translation.Success)
                {
                    regions = VerifyRegions(frame, clean, unknown, translation.Pose, canCompute);
                    translation = translation with { Accepted = regions.Verified, Regions = regions };
                    if (regions.Verified) pose = translation.Pose;
                }
            }
            RegionBudget(canCompute);
            if (!rasterVerified && !VerifyWallRaster(frame, clean, knownBoundary, unknown, pose, canCompute))
                return Finish(false, "partial-known-wall-conflict");
            RegionBudget(canCompute);
            return Finish(regions.Verified, scaleAccepted ? "partial-wall-native-similarity" : "partial-wall-fixed-linear");
        }
        catch (RegionBudgetExpiredException) { return Finish(false, "partial-wall-budget-incomplete"); }
    }

    private static MapEntryIdentityWall ProjectWall(MapEntryIdentityWall wall, MapEntryIdentityPose pose) => wall with
    {
        Normal = wall.Normal * pose.Scale + (wall.Axis == 0 ? pose.Tx : pose.Ty),
        Lo = wall.Lo * pose.Scale + (wall.Axis == 0 ? pose.Ty : pose.Tx),
        Hi = wall.Hi * pose.Scale + (wall.Axis == 0 ? pose.Ty : pose.Tx)
    };

    private static MapEntryWallRefinementEvidence FitNativeScale(IReadOnlyList<PartialPlane> groups,
        IReadOnlyList<MapEntryIdentityWall> source, MapEntryIdentityPose seed, Func<bool> canCompute)
    {
        MapEntryWallRefinementEvidence Fail(string reason) => new(false, false, seed, reason, null, 0, 0);
        var planes = new List<PartialPlane>();
        foreach (var group in groups)
        {
            RegionBudget(canCompute);
            var matching = source.Where(w => w.Axis == group.Axis && w.Sign == group.Sign &&
                Math.Abs(w.Normal - group.SourceNormal) <= 3 &&
                Math.Min(w.Hi, group.SourceHi) - Math.Max(w.Lo, group.SourceLo) >= 12).ToArray();
            if (matching.Length == 1) planes.Add(group with { SourceNormal = matching[0].Normal });
        }
        if (!SplitPlanes(planes, out var support, out var holdout, out var failure)) return Fail("wall-scale-" + failure);
        var constraints = support.Select(p => new MapEntryIdentityPlane(p.Axis, p.Sign, p.QueryNormal, p.SourceNormal, "", "")).ToArray();
        if (!MapEntryIdentityGeometry.TryFitPlaneCorrespondences(constraints, out var correction)) return Fail("wall-scale-rank-invalid");
        var errors = holdout.Select(p => Math.Abs(p.QueryNormal - correction.Scale * p.SourceNormal -
            (p.Axis == 0 ? correction.Tx : correction.Ty)));
        var summary = Summarize(errors);
        var result = new MapEntryIdentityPose(seed.Scale * correction.Scale,
            seed.Tx * correction.Scale + correction.Tx, seed.Ty * correction.Scale + correction.Ty);
        var accepted = Math.Abs(correction.Scale - 1) <= .08 && WithinWallLimits(summary) && ValidPartialPose(result);
        return new(accepted, false, result, "wall-native-uniform-scale", summary, support.Count, holdout.Count);
    }

    private static MapEntryWallRefinementEvidence FitTranslation(IReadOnlyList<PartialPlane> groups, MapEntryIdentityPose seed)
    {
        if (!SplitPlanes(groups, out var support, out var holdout, out var failure))
            return new(false, false, seed, "wall-refinement-" + failure, null, support.Count, holdout.Count);
        var dx = MapEntryIdentityFrame.Median(support.Where(p => p.Axis == 0).Select(p => p.Delta));
        var dy = MapEntryIdentityFrame.Median(support.Where(p => p.Axis == 1).Select(p => p.Delta));
        var summary = Summarize(holdout.Select(p => Math.Abs(p.Delta - (p.Axis == 0 ? dx : dy))));
        return new(WithinWallLimits(summary), false, new(seed.Scale, seed.Tx + dx, seed.Ty + dy),
            "wall-disjoint-translation", summary, support.Count, holdout.Count);
    }

    private static bool SplitPlanes(IReadOnlyList<PartialPlane> planes, out List<PartialPlane> support,
        out List<PartialPlane> holdout, out string reason)
    {
        support = []; holdout = []; reason = string.Empty;
        for (var axis = 0; axis < 2; axis++)
        {
            var ordered = planes.Where(p => p.Axis == axis).OrderBy(p => p.QueryNormal).ToArray();
            var train = ordered.Where((_, i) => i % 2 == 0).ToArray();
            var validation = ordered.Where((_, i) => i % 2 == 1).ToArray();
            if (Math.Min(train.Length, validation.Length) < 2) { reason = "disjoint-support-insufficient"; return false; }
            if (Math.Min(Spread(train), Spread(validation)) < 24) { reason = "disjoint-spread-insufficient"; return false; }
            support.AddRange(train); holdout.AddRange(validation);
        }
        return true;
    }

    private static double Spread(IEnumerable<PartialPlane> planes) =>
        planes.Max(p => p.QueryNormal) - planes.Min(p => p.QueryNormal);
    private static bool ValidPartialPose(MapEntryIdentityPose p) => p.Scale > 0 && double.IsFinite(p.Scale) && double.IsFinite(p.Tx) && double.IsFinite(p.Ty);
    private static bool WithinWallLimits(MapEntryIdentityHeldOut s) => s.MedianPixels <= 3 && s.P90Pixels <= 8 && s.MaxPixels <= 20;
    private static MapEntryIdentityHeldOut Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return new(0, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);
        var at = (sorted.Length - 1) * .9;
        return new(sorted.Length, MapEntryIdentityFrame.Median(sorted),
            sorted[(int)at] + (at - (int)at) * (sorted[(int)Math.Ceiling(at)] - sorted[(int)at]), sorted[^1]);
    }
}
