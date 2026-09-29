namespace IDVBuff.Features.Maps;

public static partial class MapEntryCornerIdentity
{
    public static MapEntryCornerIdentitySearch RankMain(IReadOnlyList<MapEntryCornerRegion> regions,
        IReadOnlyList<MapEntryCornerNode> query, string floorKey, int frameWidth, int frameHeight,
        Func<bool> canCompute, Func<MapEntryCornerIdentityCandidate, MapEntryCornerVerification>? verifyCandidate = null)
    {
        if (query.Any(n => !ValidNode(n)) || frameWidth <= 0 || frameHeight <= 0 || string.IsNullOrWhiteSpace(floorKey))
            return Empty("entry-invalid-corner-observation", false);
        var items = regions.Where(r => r.FloorKey == floorKey).ToArray();
        if (items.Length == 0) return Empty("entry-floor-not-indexed");
        if (items.Any(r => !ValidRegion(r))) return Empty("entry-invalid-corner-resource", false);
        if (query.Count < 3) return Empty("entry-visible-structure-insufficient");
        var rows = new List<MapEntryCornerIdentityCandidate>();
        var complete = true;
        try
        {
            foreach (var region in items)
            {
                CheckBudget(canCompute);
                var oriented = OrientedPairs(query, region.Nodes, canCompute);
                var matcher = new CornerMatcher(query, region.Nodes, oriented);
                var mainIndices = Enumerable.Range(0, region.Nodes.Count).Where(i => region.Nodes[i].IsMain).ToArray();
                var queryBySource = oriented.GroupBy(p => p.SourceIndex).ToDictionary(g => g.Key, g => g.Select(p => p.QueryIndex).ToArray());
                var hypotheses = new List<MapEntryCornerIdentityCandidate>();
                for (var ai = 0; ai < mainIndices.Length; ai++)
                for (var bi = ai + 1; bi < mainIndices.Length; bi++)
                {
                    CheckBudget(canCompute);
                    var sa = mainIndices[ai];
                    var sb = mainIndices[bi];
                    if (!queryBySource.TryGetValue(sa, out var qas) || !queryBySource.TryGetValue(sb, out var qbs)) continue;
                    // NumPy meshgrid/ravel visits qB first, then qA; retain deterministic source order.
                    foreach (var qb in qbs)
                    foreach (var qa in qas)
                    {
                        CheckBudget(canCompute);
                        if (!TryPairPose(region.Nodes[sa].Point, region.Nodes[sb].Point, query[qa].Point, query[qb].Point,
                                region.Unit, .7, 2, Math.Min(frameWidth, frameHeight) / 4.0, out var seed)) continue;
                        ScoreMain(region, query, matcher, seed, hypotheses, canCompute);
                    }
                }
                var ranked = hypotheses.OrderByDescending(h => h.Score).ToArray();
                if (ranked.Length > 0)
                    rows.Add(ranked[0] with { PoseAmbiguous = ranked.Length > 1 && ranked[1].Score >= ranked[0].Score - 1 });
            }
        }
        catch (BudgetExpiredException) { complete = false; }
        rows = rows.OrderByDescending(row => row.Score).ToList();
        var cutoff = rows.Count > 0 ? Math.Max(10, rows[0].Score * .65) : 10;
        return VerifyMain(rows, cutoff, complete, canCompute, verifyCandidate);
    }

    private static void ScoreMain(MapEntryCornerRegion region, IReadOnlyList<MapEntryCornerNode> query,
        CornerMatcher matcher, MapEntryIdentityPose seed,
        List<MapEntryCornerIdentityCandidate> hypotheses, Func<bool> canCompute)
    {
        var anchor = seed.Apply(region.CenterSource);
        if (hypotheses.Any(h => Distance(anchor, h.Pose.Apply(region.CenterSource)) < 3 &&
                Math.Abs(seed.Scale - h.Pose.Scale) * region.Unit < .25)) return;
        var pose = seed;
        IReadOnlyList<MapEntryCornerMatch> matches = [];
        var mainCount = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            matches = matcher.Match(pose, canCompute);
            if (matches.Count < 4) return;
            mainCount = matches.Count(p => region.Nodes[p.SourceIndex].IsMain);
            if (mainCount < 2) return;
            if (iteration < 2 && !FitPoints(query, region.Nodes, matches, out pose)) return;
        }
        var refined = new MapEntryCornerIdentityCandidate
        {
            Region = region, Pose = pose, Matches = matches, EntryKind = "main", MainChains = mainCount,
            ExtentGrid = Extent(region, matches),
            NeighborExtentGrid = Extent(region, matches.Where(p => !region.Nodes[p.SourceIndex].IsMain))
        };
        for (var index = 0; index < hypotheses.Count; index++)
        {
            var old = hypotheses[index];
            if (Distance(pose.Apply(region.CenterSource), old.Pose.Apply(region.CenterSource)) +
                    Math.Abs(pose.Scale - old.Pose.Scale) * region.Unit * 12 > 3) continue;
            if (refined.Score > old.Score) hypotheses[index] = refined;
            return;
        }
        hypotheses.Add(refined);
    }

    private static MapEntryCornerIdentitySearch VerifyMain(List<MapEntryCornerIdentityCandidate> rows,
        double cutoff, bool retrievalComplete, Func<bool> canCompute,
        Func<MapEntryCornerIdentityCandidate, MapEntryCornerVerification>? verifyCandidate)
    {
        var competitorCount = rows.Count(r => r.Score >= cutoff);
        var verificationComplete = retrievalComplete && competitorCount is > 0 and <= 3 && verifyCandidate is not null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var reason = !retrievalComplete ? "entry-search-incomplete-unverified" : row.Score < cutoff ?
                "entry-outside-score-band-unverified" : competitorCount > 3 ? "entry-competition-limit-unverified" :
                verifyCandidate is null ? "entry-validator-unavailable" : "entry-verification-budget-incomplete";
            if (verificationComplete && row.Score >= cutoff)
            {
                if (!canCompute()) verificationComplete = false;
                else
                {
                    var verification = verifyCandidate!(row);
                    if (!ValidPose(verification.Pose)) verification = verification with { Success = false, Reason = "entry-invalid-verified-pose" };
                    row = row with { VerificationEvaluated = true, Verification = verification };
                    reason = verification.Reason;
                    if (!canCompute()) verificationComplete = false;
                }
            }
            rows[i] = row with { CompetitionComplete = retrievalComplete, Reason = reason };
        }
        var accepted = rows.Where(r => r.Score >= cutoff && r.Verification is { Success: true, ExitContradicted: false }).ToArray();
        if (verificationComplete && accepted.Length == 1)
        {
            var winner = accepted[0];
            var eligible = !winner.PoseAmbiguous && winner.NeighborChains >= 6 && winner.Score >= 10 &&
                (winner.MainChains >= 3 || winner.NeighborChains >= 8 &&
                    Math.Min(winner.NeighborExtentGrid.X, winner.NeighborExtentGrid.Y) >= 4);
            if (eligible) rows[rows.IndexOf(winner)] = winner with { IdentitySupported = true };
        }
        var ordered = rows.OrderByDescending(r => r.IdentitySupported).ThenByDescending(r => r.Score).ToArray();
        return new(ordered, retrievalComplete, verificationComplete, competitorCount,
            !ordered.Any(r => r.IdentitySupported), ordered.Any(r => r.IdentitySupported) ? "entry-identity-verified" :
            retrievalComplete ? "entry-neighborhood-candidates" : "entry-budget-incomplete");
    }

    public static MapEntryCornerIdentityDecision? SelectMain(MapEntryCornerIdentitySearch search)
    {
        if (!search.RetrievalCompleted || !search.VerificationComplete) return null;
        var winner = search.Candidates.SingleOrDefault(row => row.IdentitySupported);
        return winner is null ? null : new(winner, false, [winner], "complete-main-entry-neighborhood-and-current-author");
    }
}
