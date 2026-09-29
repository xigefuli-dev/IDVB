namespace IDVBuff.Features.Maps;

/// <summary>
/// Exit-anchored geometry from candidate 9491d0b0. No resource reads, OpenCV, session writes,
/// identity authorization, or cross-floor seeds. Coordinates and tolerances are current-frame pixels.
/// </summary>
public static partial class MapEntryIdentityGeometry
{
    private static readonly double[] SeedRatios = [1, .97, 1.03, .94, 1.06];

    public static MapEntryIdentityGeometrySearch Evaluate(
        IReadOnlyList<MapEntryIdentityPassport> passports,
        IReadOnlyList<MapEntryIdentityObservation> observations,
        IReadOnlyList<MapEntryIdentityWall> observedWalls,
        string floorKey, Func<int, int, bool> visibleFloor, int frameWidth, int frameHeight,
        Func<bool> canCompute, Func<MapEntryIdentityGeometryCandidate, bool>? verifyCandidate = null)
    {
        var candidates = new List<MapEntryIdentityGeometryCandidate>();
        var examined = new List<MapEntryIdentityGeometryExamination>();
        var complete = true;
        MapEntryIdentityGeometrySearch Finish(string reason, bool first = false) =>
            new(candidates.ToArray(), examined.ToArray(), complete && !first, first, reason);
        if (string.IsNullOrWhiteSpace(floorKey) || frameWidth < 3 || frameHeight < 3 ||
            observedWalls.Any(wall => !ValidWall(wall)))
        {
            complete = false;
            return Finish("entry-invalid-observation-geometry");
        }
        var items = passports.Where(p => p.FloorKey == floorKey).ToArray();
        if (items.Length == 0) return Finish("entry-passport-unavailable");
        if (items.Any(item => !ValidPassport(item)))
        {
            complete = false;
            return Finish("entry-invalid-passport-geometry");
        }
        var reliable = observations.Select((value, index) => (Value: value, Index: index))
            .Where(o => Reliable(o.Value)).ToArray();
        if (reliable.Length == 0) return Finish("entry-no-reliable-exit");
        try
        {
            CheckBudget(canCompute);
            foreach (var (observation, observationIndex) in reliable)
            {
                if (!TryExitGeometry(observation, out var width, out var terminalCenter))
                {
                    complete = false;
                    examined.Add(new("", floorKey, "", observationIndex, "exit-direction-or-local-width-unresolved", 0));
                    continue;
                }
                foreach (var item in items)
                {
                    CheckBudget(canCompute);
                    if (item.DirectionKnown && !item.OutwardDirections.Contains(observation.Direction))
                    {
                        examined.Add(new(item.MapId, floorKey, item.SourceEntryId, observationIndex,
                            "source-exit-direction-incompatible", 0));
                        continue;
                    }
                    var initialScale = width / item.Unit;
                    var fitCache = new Dictionary<string, FitAttempt>(StringComparer.Ordinal);
                    var fits = new List<WallFit>();
                    var seen = new HashSet<WallFit>(ReferenceEqualityComparer.Instance);
                    var lastReason = "exit-local-wall-geometry-incompatible";
                    foreach (var ratio in SeedRatios)
                    foreach (var anchor in new[] { terminalCenter, observation.IconCenter })
                    {
                        CheckBudget(canCompute);
                        var scale = initialScale * ratio;
                        var attempt = FitLocalWalls(observedWalls, item.KnownWalls,
                            new(scale, anchor.X - scale * item.CenterSource.X, anchor.Y - scale * item.CenterSource.Y),
                            fitCache, canCompute);
                        lastReason = attempt.Reason;
                        if (attempt.Fit is not { } fit) continue;
                        if (!BoundToExit(fit.Pose, item.CenterSource, observation.IconCenter, width, initialScale))
                        {
                            lastReason = "entry-fit-outside-exit-bounds";
                            continue;
                        }
                        if (seen.Add(fit)) fits.Add(fit);
                    }
                    examined.Add(new(item.MapId, floorKey, item.SourceEntryId, observationIndex,
                        fits.Count > 0 ? "local-wall-pose-candidates" : lastReason, fits.Count));
                    foreach (var fit in fits)
                    {
                        CheckBudget(canCompute);
                        var chain = new List<WallFit> { fit };
                        var converged = false;
                        for (var iteration = 0; iteration < 4; iteration++)
                        {
                            CheckBudget(canCompute);
                            var previous = chain[^1];
                            var attempt = FitLocalWalls(observedWalls, item.KnownWalls, previous.Pose, fitCache, canCompute);
                            if (attempt.Fit is not { } next) break;
                            if (next.Pose == previous.Pose) { converged = true; break; }
                            if (!BoundToExit(next.Pose, item.CenterSource, observation.IconCenter, width, initialScale) ||
                                chain.Any(prior => prior.Pose == next.Pose)) break;
                            chain.Add(next);
                            if (!CorrespondenceExtends(previous, next)) break;
                        }
                        var history = chain.Select(f => new MapEntryIdentityRefinement(f.Pose, f.Planes, f.HeldOut)).ToArray();
                        // Newest first, but an unsuccessful refined result never discards its predecessors.
                        for (var index = chain.Count - 1; index >= 0; index--)
                        {
                            CheckBudget(canCompute);
                            var current = chain[index];
                            var rooms = RoomEvidence(item, current.Pose, observedWalls,
                                visibleFloor, frameWidth, frameHeight, canCompute);
                            var candidate = new MapEntryIdentityGeometryCandidate
                            {
                                Passport = item, Observation = observation, ObservationIndex = observationIndex,
                                Pose = current.Pose, Planes = current.Planes, HeldOut = current.HeldOut,
                                Rooms = rooms, RefinementHistory = history, RefinementConverged = converged,
                                Reason = rooms.Count > 0 ? "entry-local-geometry-verified" : "entry-room-port-unobserved"
                            };
                            if (verifyCandidate is not null)
                            {
                                var verified = verifyCandidate(candidate);
                                CheckBudget(canCompute);
                                candidate = candidate with { ExternalVerificationPassed = verified };
                            }
                            candidates.Add(candidate);
                            if (candidate.LocalGeometryVerified && candidate.ExternalVerificationPassed)
                                return Finish("entry-first-verified", first: true);
                        }
                        if (candidates.Count > 64)
                        {
                            complete = false;
                            return Finish("entry-candidate-budget-incomplete");
                        }
                    }
                }
            }
        }
        catch (BudgetExpiredException)
        {
            complete = false;
            return Finish("entry-budget-incomplete");
        }
        var families = candidates.Where(c => c.LocalGeometryVerified).Select(c => c.Passport.Family).ToArray();
        if (families.Contains("main") || !families.Contains("side")) return Finish("entry-family-unresolved");
        return Finish(complete ? (verifyCandidate is null ? "entry-local-geometry-complete" : "entry-local-verification-failed") :
            "entry-local-observation-incomplete");
    }

    private static bool Reliable(MapEntryIdentityObservation observation) =>
        double.IsFinite(observation.IconScore) && (observation.IconScore >= .78 ||
            observation.IconScore >= .58 && observation.DirectionReliable);

    private static bool TryExitGeometry(MapEntryIdentityObservation observation, out double width,
        out MapEntryIdentityPoint terminalCenter)
    {
        width = 0;
        terminalCenter = default;
        if (!observation.DirectionReliable || !Finite(observation.IconCenter) ||
            observation.Rectangle is not { } rectangle || !double.IsFinite(rectangle.Left) ||
            !double.IsFinite(rectangle.Top) || !double.IsFinite(rectangle.Right) || !double.IsFinite(rectangle.Bottom) ||
            rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top) return false;
        width = observation.Direction is MapEntryIdentityDirection.East or MapEntryIdentityDirection.West
            ? rectangle.Bottom - rectangle.Top : rectangle.Right - rectangle.Left;
        terminalCenter = observation.Direction switch
        {
            MapEntryIdentityDirection.West => new(rectangle.Left + width / 2, (rectangle.Top + rectangle.Bottom) / 2),
            MapEntryIdentityDirection.East => new(rectangle.Right - width / 2, (rectangle.Top + rectangle.Bottom) / 2),
            MapEntryIdentityDirection.North => new((rectangle.Left + rectangle.Right) / 2, rectangle.Top + width / 2),
            MapEntryIdentityDirection.South => new((rectangle.Left + rectangle.Right) / 2, rectangle.Bottom - width / 2),
            _ => default
        };
        return observation.Direction is MapEntryIdentityDirection.North or MapEntryIdentityDirection.East or
            MapEntryIdentityDirection.South or MapEntryIdentityDirection.West;
    }

    private static bool BoundToExit(MapEntryIdentityPose pose, MapEntryIdentityPoint sourceCenter,
        MapEntryIdentityPoint iconCenter, double width, double initialScale)
    {
        var mapped = pose.Apply(sourceCenter);
        return Distance(mapped, iconCenter) <= Math.Max(6, width * .2) && Math.Abs(pose.Scale / initialScale - 1) <= .08;
    }

    private static IReadOnlyList<MapEntryIdentityRoomWitness> RoomEvidence(MapEntryIdentityPassport item,
        MapEntryIdentityPose pose, IReadOnlyList<MapEntryIdentityWall> query,
        Func<int, int, bool> visibleFloor, int frameWidth, int frameHeight, Func<bool> canCompute)
    {
        var witnesses = new List<MapEntryIdentityRoomWitness>();
        foreach (var room in item.Rooms)
        {
            CheckBudget(canCompute);
            if (room.Kind != "room" || room.ConnectionPath.Count == 0) continue;
            var planes = new List<MapEntryIdentityWall>();
            foreach (var source in room.KnownWalls)
            {
                CheckBudget(canCompute);
                var normal = pose.Scale * source.Normal + (source.Axis == 0 ? pose.Tx : pose.Ty);
                var tangentShift = source.Axis == 0 ? pose.Ty : pose.Tx;
                var lo = pose.Scale * source.Lo + tangentShift;
                var hi = pose.Scale * source.Hi + tangentShift;
                for (var qi = 0; qi < query.Count; qi++)
                {
                    if ((qi & 127) == 0) CheckBudget(canCompute);
                    var line = query[qi];
                    if (line.Axis != source.Axis || line.Sign != source.Sign || Math.Abs(normal - line.Normal) > 3 ||
                        Math.Min(hi, line.Hi) - Math.Max(lo, line.Lo) < 12) continue;
                    if (!planes.Any(p => p.Axis == line.Axis && p.Sign == line.Sign && p.Normal == line.Normal))
                        planes.Add(line);
                }
            }
            var ports = new List<string>();
            foreach (var port in room.Ports)
            {
                if (port.Name != room.ConnectingPort) continue;
                var point = pose.Apply(port.CenterSource);
                if (!Finite(point)) continue;
                var px = Math.Round(point.X, MidpointRounding.ToEven);
                var py = Math.Round(point.Y, MidpointRounding.ToEven);
                if (px < 1 || px >= frameWidth - 1 || py < 1 || py >= frameHeight - 1) continue;
                var x = (int)px;
                var y = (int)py;
                var count = 0;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (visibleFloor(x + dx, y + dy)) count++;
                if (count >= 5) ports.Add(port.Name);
            }
            if (planes.Count >= 2 && ports.Count > 0)
                witnesses.Add(new(room.InstanceId, planes.ToArray(), ports.ToArray(), room.ConnectionPath));
        }
        return witnesses;
    }

    private static bool Finite(MapEntryIdentityPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
    private static double Distance(MapEntryIdentityPoint a, MapEntryIdentityPoint b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static bool ValidWall(MapEntryIdentityWall wall) => wall.Axis is 0 or 1 && wall.Sign is -1 or 1 &&
        double.IsFinite(wall.Normal) && double.IsFinite(wall.Lo) && double.IsFinite(wall.Hi) && wall.Hi > wall.Lo;
    private static bool ValidPassport(MapEntryIdentityPassport item) => Finite(item.CenterSource) &&
        double.IsFinite(item.Unit) && item.Unit > 0 && item.KnownWalls.All(ValidWall) &&
        item.MainOutlineSource.All(Finite) &&
        item.Rooms.All(room => room.KnownWalls.All(ValidWall) && room.Ports.All(port => Finite(port.CenterSource)));
}
