namespace IDVBuff.Features.Maps;

public static partial class MapEntryCornerIdentity
{
    public static MapEntryCornerIdentitySearch RankLocal(IReadOnlyList<MapEntryCornerRegion> regions,
        IReadOnlyList<MapEntryCornerNode> query, IReadOnlyList<MapEntryCornerPair> queryPairs,
        string floorKey, int frameWidth, int frameHeight, Func<bool> canCompute,
        Func<MapEntryCornerIdentityCandidate, MapEntryCornerVerification>? verifyCandidate = null)
    {
        if (query.Any(n => !ValidNode(n)) || frameWidth <= 0 || frameHeight <= 0 || string.IsNullOrWhiteSpace(floorKey) ||
            queryPairs.Any(p => p.From < 0 || p.From >= query.Count || p.To < 0 || p.To >= query.Count))
            return Empty("side-invalid-corner-observation", false);
        var items = regions.Where(r => r.FloorKey == floorKey).ToArray();
        if (items.Any(r => !ValidRegion(r))) return Empty("side-invalid-corner-resource", false);
        if (query.Count < 4 || queryPairs.Count == 0) return Empty("side-visible-structure-insufficient");
        var rows = new List<MapEntryCornerIdentityCandidate>();
        var complete = true;
        try
        {
            foreach (var region in items)
            {
                CheckBudget(canCompute);
                if (region.Pairs.Count == 0) continue;
                var oriented = OrientedPairs(query, region.Nodes, canCompute);
                var hypotheses = new List<MapEntryCornerIdentityCandidate>();
                var seen = new HashSet<(double Scale, double X, double Y)>();
                foreach (var qp in queryPairs)
                foreach (var sp in region.Pairs)
                {
                    CheckBudget(canCompute);
                    if (!Oriented(query[qp.From], region.Nodes[sp.From]) || !Oriented(query[qp.To], region.Nodes[sp.To])) continue;
                    if (!TryPairPose(region.Nodes[sp.From].Point, region.Nodes[sp.To].Point,
                            query[qp.From].Point, query[qp.To].Point, region.Unit, .6, 3,
                            Math.Min(frameWidth, frameHeight) / 4.0, out var pose)) continue;
                    if (!seen.Add((Math.Round(pose.Scale * region.Unit * 4, MidpointRounding.ToEven),
                            Math.Round(pose.Tx / 3, MidpointRounding.ToEven), Math.Round(pose.Ty / 3, MidpointRounding.ToEven)))) continue;
                    var matches = UniqueMatches(query, region.Nodes, oriented, pose, canCompute);
                    if (matches.Count < 4) continue;
                    var extent = Extent(region, matches);
                    if (Math.Min(extent.X, extent.Y) < 1) continue;
                    if (hypotheses.Any(h => Distance(new(pose.Tx, pose.Ty), new(h.Pose.Tx, h.Pose.Ty)) +
                            Math.Abs(pose.Scale - h.Pose.Scale) * region.Unit < 3)) continue;
                    hypotheses.Add(LocalCandidate(region, pose, matches));
                }
                var continuous = new List<MapEntryCornerIdentityCandidate>();
                foreach (var initial in hypotheses.OrderByDescending(h => h.Score).Take(3))
                {
                    var matches = initial.Matches;
                    var pose = initial.Pose;
                    var solved = true;
                    for (var iteration = 0; iteration < 3; iteration++)
                    {
                        CheckBudget(canCompute);
                        if (!FitPoints(query, region.Nodes, matches, out pose)) { solved = false; break; }
                        var updated = UniqueMatches(query, region.Nodes, oriented, pose, canCompute);
                        if (updated.Count < 4) break;
                        matches = updated;
                    }
                    if (!solved || pose.Scale * region.Unit < 8 || pose.Scale * region.Unit > Math.Min(frameWidth, frameHeight) / 4.0) continue;
                    continuous.Add(LocalCandidate(region, pose, matches));
                }
                var finalists = new List<MapEntryCornerIdentityCandidate>();
                foreach (var current in continuous.OrderByDescending(h => h.Score))
                {
                    CheckBudget(canCompute);
                    if (finalists.Any(old => MaximumDisplacement(region, current.Pose, old.Pose) <= 3)) continue;
                    finalists.Add(current);
                }
                rows.AddRange(finalists);
            }
        }
        catch (BudgetExpiredException) { complete = false; }
        rows = rows.OrderByDescending(r => r.Score).ToList();
        var cutoff = rows.Count > 0 ? Math.Max(4, rows[0].Score - 1) : 4;
        return VerifyLocal(rows, cutoff, complete, canCompute, verifyCandidate);
    }

    private static MapEntryCornerIdentityCandidate LocalCandidate(MapEntryCornerRegion region,
        MapEntryIdentityPose pose, IReadOnlyList<MapEntryCornerMatch> matches)
    {
        var nearGate = region.GateCenters.Any(gate => matches.Any(pair =>
            Distance(region.Nodes[pair.SourceIndex].Point, gate) <= 12 * region.Unit));
        return new()
        {
            Region = region, Pose = pose, Matches = matches,
            EntryKind = nearGate ? "side-neighborhood" : "local-structure", ExtentGrid = Extent(region, matches)
        };
    }

    private static MapEntryCornerIdentitySearch VerifyLocal(List<MapEntryCornerIdentityCandidate> rows,
        double cutoff, bool retrievalComplete, Func<bool> canCompute,
        Func<MapEntryCornerIdentityCandidate, MapEntryCornerVerification>? verifyCandidate)
    {
        var competitors = rows.Where(r => r.Score >= cutoff).ToArray();
        var verificationComplete = retrievalComplete && competitors.Length <= 3 && verifyCandidate is not null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var reason = !retrievalComplete ? "side-search-incomplete-unverified" : row.Score < cutoff ?
                "side-outside-score-band-unverified" : competitors.Length > 3 ? "side-competition-limit-unverified" :
                verifyCandidate is null ? "side-validator-unavailable" : "side-verification-budget-incomplete";
            if (verificationComplete && row.Score >= cutoff)
            {
                if (!canCompute()) verificationComplete = false;
                else
                {
                    var verification = verifyCandidate!(row);
                    if (!ValidPose(verification.Pose)) verification = verification with { Success = false, Reason = "side-invalid-verified-pose" };
                    row = row with { VerificationEvaluated = true, Verification = verification };
                    reason = verification.Reason;
                    if (!canCompute()) verificationComplete = false;
                }
            }
            rows[i] = row with
            {
                CompetitionComplete = retrievalComplete, Reason = reason,
                PoseAmbiguous = competitors.Count(other => SameIdentity(row, other)) > 1
            };
        }
        return new(rows.ToArray(), retrievalComplete, verificationComplete, competitors.Length,
            competitors.Select(row => (row.Region.MapId, row.Region.FloorKey)).Distinct().Count() != 1,
            retrievalComplete ? "side-local-candidates" : "side-budget-incomplete");
    }
}
