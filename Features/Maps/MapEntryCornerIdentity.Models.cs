namespace IDVBuff.Features.Maps;

/// <summary>Rays preserve contour order; they are measured unit vectors, not unordered corner labels.</summary>
public sealed record MapEntryCornerNode(MapEntryIdentityPoint Point,
    MapEntryIdentityPoint Ray0, MapEntryIdentityPoint Ray1, bool IsMain = false);

public readonly record struct MapEntryCornerPair(int From, int To);
public readonly record struct MapEntryCornerMatch(int QueryIndex, int SourceIndex);

public sealed record MapEntryCornerRegion(string MapId, string FloorKey, string RegionId, string ProfileId,
    double Unit, MapEntryIdentityPoint CenterSource, IReadOnlyList<MapEntryCornerNode> Nodes,
    IReadOnlyList<MapEntryCornerPair> Pairs, IReadOnlyList<MapEntryIdentityPoint> GateCenters);

/// <summary>Success means the caller actually checked the current author, walls, and exit evidence.</summary>
public sealed record MapEntryCornerVerification(bool Success, MapEntryIdentityPose Pose,
    bool ExitContradicted = false, string Reason = "");

public sealed record MapEntryCornerIdentityCandidate
{
    public required MapEntryCornerRegion Region { get; init; }
    public required MapEntryIdentityPose Pose { get; init; }
    public required IReadOnlyList<MapEntryCornerMatch> Matches { get; init; }
    public required string EntryKind { get; init; }
    public int ChainCount => Matches.Count;
    public double Score => ChainCount;
    public int MainChains { get; init; }
    public int NeighborChains => ChainCount - MainChains;
    public MapEntryIdentityPoint ExtentGrid { get; init; }
    public MapEntryIdentityPoint NeighborExtentGrid { get; init; }
    public bool PoseAmbiguous { get; init; }
    public bool CompetitionComplete { get; init; }
    public bool VerificationEvaluated { get; init; }
    public MapEntryCornerVerification? Verification { get; init; }
    public bool IdentitySupported { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record MapEntryCornerIdentitySearch(IReadOnlyList<MapEntryCornerIdentityCandidate> Candidates,
    bool RetrievalCompleted, bool VerificationComplete, int CompetitiveHypotheses,
    bool IdentityAmbiguous, string Reason);

/// <summary>Evidence only. The session owner must validate frame/floor/context before locking identity.</summary>
public sealed record MapEntryCornerIdentityDecision(MapEntryCornerIdentityCandidate Candidate,
    bool IdentityOnly, IReadOnlyList<MapEntryCornerIdentityCandidate> PoseCandidates, string Evidence);
