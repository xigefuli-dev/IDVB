namespace IDVBuff.Features.Maps;

public static partial class MapEntryCornerIdentity
{
    /// <summary>
    /// The strict 9491 consumer: ordinary verification failure does not remove a different-map competitor.
    /// Only an explicit current-exit contradiction can remove it, without promoting a lower score band.
    /// </summary>
    public static MapEntryCornerIdentityDecision? SelectLocal(MapEntryCornerIdentitySearch search,
        string floorKey, IReadOnlyList<MapEntryCornerIdentityCandidate> mainCandidates, int frameWidth, int frameHeight)
    {
        if (!search.RetrievalCompleted || !search.VerificationComplete || search.Candidates.Count == 0) return null;
        var cutoff = Math.Max(4, search.Candidates.Max(row => row.Score) - 1);
        var original = search.Candidates.Where(row => row.Score >= cutoff).ToArray();
        if (original.Length != search.CompetitiveHypotheses) return null;
        var remaining = original.Where(row => row.Verification?.ExitContradicted != true).ToArray();
        if (remaining.Length == original.Length)
        {
            if (search.IdentityAmbiguous) return null;
        }
        else if (remaining.Where(row => row.Verification?.Success == true)
                     .Select(row => (row.Region.MapId, row.Region.FloorKey)).Distinct().Count() > 1) return null;
        if (remaining.Length == 0 || remaining.Select(row => (row.Region.MapId, row.Region.FloorKey)).Distinct().Count() != 1)
            return null;
        if (remaining.Any(row => !LocalEvidence(row, floorKey))) return null;
        var winner = remaining[0];
        var main = mainCandidates.Where(row => row.Verification is { Success: true, ExitContradicted: false }).ToArray();
        if (main.Any(row => !SameIdentity(row, winner))) return null;
        if (remaining.Length > 1)
            return new(winner, true, remaining, "complete-same-map-side-competition-pose-unresolved");
        if (winner.PoseAmbiguous) return null;
        var verifiedPose = winner.Verification!.Pose;
        var corners = new MapEntryIdentityPoint[] { new(0, 0), new(frameWidth, 0), new(0, frameHeight), new(frameWidth, frameHeight) };
        foreach (var row in main)
        {
            var mainPose = row.Verification!.Pose;
            if (!ValidPose(mainPose)) return null;
            foreach (var point in corners)
            {
                var sourcePoint = new MapEntryIdentityPoint((point.X - mainPose.Tx) / mainPose.Scale, (point.Y - mainPose.Ty) / mainPose.Scale);
                if (Distance(verifiedPose.Apply(sourcePoint), point) > 3) return null;
            }
        }
        return new(winner, false, [winner], "complete-unique-side-structure-and-current-author");
    }

    private static bool LocalEvidence(MapEntryCornerIdentityCandidate row, string floorKey) =>
        row.CompetitionComplete && row.Region.FloorKey == floorKey && row.ChainCount >= 6 &&
        Math.Min(row.ExtentGrid.X, row.ExtentGrid.Y) >= 2 && row.VerificationEvaluated &&
        row.Verification is { Success: true, ExitContradicted: false } verification && ValidPose(verification.Pose);

    private static bool SameIdentity(MapEntryCornerIdentityCandidate a, MapEntryCornerIdentityCandidate b) =>
        a.Region.MapId == b.Region.MapId && a.Region.FloorKey == b.Region.FloorKey;
}
