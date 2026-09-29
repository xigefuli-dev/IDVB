namespace IDVBuff.Features.Maps;

public readonly record struct MapEntryIdentityPoint(double X, double Y);

public readonly record struct MapEntryIdentityRectangle(double Left, double Top, double Right, double Bottom);

public enum MapEntryIdentityDirection { Unknown, North, East, South, West }

/// <summary>Axis is the normal axis (0 = X, 1 = Y); Sign distinguishes opposing wall faces.</summary>
public sealed record MapEntryIdentityWall(
    int Axis, int Sign, double Normal, double Lo, double Hi, string EdgeId);

/// <summary>The port center has already been converted from local grid to source pixels.</summary>
public sealed record MapEntryIdentityPort(string Name, MapEntryIdentityPoint CenterSource);

public sealed record MapEntryIdentityRoom(
    string InstanceId, string Kind, IReadOnlyList<string> ConnectionPath,
    string ConnectingPort, IReadOnlyList<MapEntryIdentityWall> KnownWalls,
    IReadOnlyList<MapEntryIdentityPort> Ports);

/// <summary>All geometry uses one source coordinate system, without cross-floor scale conversion.</summary>
public sealed record MapEntryIdentityPassport(
    string MapId, string FloorKey, string SourceEntryId, string Family,
    MapEntryIdentityPoint CenterSource, double Unit, bool DirectionKnown,
    IReadOnlyList<MapEntryIdentityDirection> OutwardDirections,
    IReadOnlyList<MapEntryIdentityWall> KnownWalls, IReadOnlyList<MapEntryIdentityRoom> Rooms)
{
    /// <summary>Optional measured main-entry polygon, already converted into source pixels.</summary>
    public IReadOnlyList<MapEntryIdentityPoint> MainOutlineSource { get; init; } = [];
}

public sealed record MapEntryIdentityObservation(
    MapEntryIdentityPoint IconCenter, double IconScore,
    MapEntryIdentityDirection Direction, bool DirectionReliable,
    MapEntryIdentityRectangle? Rectangle);

/// <summary>A source-to-current-frame hypothesis; never a trusted display transform.</summary>
public readonly record struct MapEntryIdentityPose(double Scale, double Tx, double Ty)
{
    public MapEntryIdentityPoint Apply(MapEntryIdentityPoint point) =>
        new(Scale * point.X + Tx, Scale * point.Y + Ty);
}

public sealed record MapEntryIdentityPlane(
    int Axis, int Sign, double QueryNormal, double SourceNormal,
    string QueryEdge, string SourceEdge);

public sealed record MapEntryIdentityHeldOut(int Count, double MedianPixels, double P90Pixels, double MaxPixels);

public sealed record MapEntryIdentityRoomWitness(
    string InstanceId, IReadOnlyList<MapEntryIdentityWall> SupportedPlanes,
    IReadOnlyList<string> VisiblePorts, IReadOnlyList<string> ConnectionPath);

public sealed record MapEntryIdentityRefinement(
    MapEntryIdentityPose Pose, IReadOnlyList<MapEntryIdentityPlane> Planes,
    MapEntryIdentityHeldOut HeldOut);

public sealed record MapEntryIdentityWallFit(bool Success, MapEntryIdentityPose Pose,
    IReadOnlyList<MapEntryIdentityPlane> Planes, MapEntryIdentityHeldOut? HeldOut, string Reason);

public sealed record MapEntryIdentityGeometryCandidate
{
    public required MapEntryIdentityPassport Passport { get; init; }
    public required MapEntryIdentityObservation Observation { get; init; }
    public required int ObservationIndex { get; init; }
    public required MapEntryIdentityPose Pose { get; init; }
    public required IReadOnlyList<MapEntryIdentityPlane> Planes { get; init; }
    public required MapEntryIdentityHeldOut HeldOut { get; init; }
    public required IReadOnlyList<MapEntryIdentityRoomWitness> Rooms { get; init; }
    public required IReadOnlyList<MapEntryIdentityRefinement> RefinementHistory { get; init; }
    public bool RefinementConverged { get; init; }
    public bool LocalGeometryVerified => Rooms.Count > 0;
    public bool ExternalVerificationPassed { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record MapEntryIdentityGeometryExamination(
    string MapId, string FloorKey, string SourceEntryId, int ObservationIndex,
    string Reason, int PartialPoses);

public sealed record MapEntryIdentityExitCheck(int ObservationIndex, bool Applicable, bool Contradicted,
    bool MainFamilyPossible, IReadOnlyList<string> SourceEntryIds, string Reason);

public sealed record MapEntryIdentityExitEvidence(bool Completed, bool CoverageComplete,
    IReadOnlyList<MapEntryIdentityExitCheck> Checks)
{
    public bool Applicable => Checks.Any(check => check.Applicable);
    public bool Contradicted => Checks.Any(check => check.Contradicted);
}

/// <summary>
/// Completed means all applicable geometry was examined. It grants no identity or display authority.
/// FirstVerified requires the caller's current-frame author/exit validator; competition remains incomplete.
/// </summary>
public sealed record MapEntryIdentityGeometrySearch(
    IReadOnlyList<MapEntryIdentityGeometryCandidate> Candidates,
    IReadOnlyList<MapEntryIdentityGeometryExamination> Examined,
    bool Completed, bool FirstVerified, string Reason)
{
    public bool CompetitionComplete => Completed && !FirstVerified;
}
