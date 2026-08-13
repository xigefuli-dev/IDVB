namespace IDVBuff.Features.Maps;

public enum SideEntranceCandidateDisposition
{
    Reliable,
    NeedsVerification,
    Rejected
}

public enum SideEntranceRejectionReason
{
    None,
    WeakTemplateSimilarity,
    AmbiguousTemplateRanking,
    GateSpatialMismatch,
    ScaleAtSearchBoundary,
    InvalidFeatureData,
    StructureRejected,
    IdentityConfidenceTooLow
}

public enum SideEntranceGateAssociationKind
{
    None,
    DetectedGate,
    TemplateOnlyRescue
}

/// <summary>侧门扫描的单条证据结果。模板相似度本身不是地图置信度。</summary>
public sealed class SideEntranceScanCandidate
{
    public MapRecord Map { get; init; } = new();
    public string FloorKey { get; init; } = string.Empty;
    public double MatchScore { get; init; }
    public MapScreenRect MatchLocation { get; set; }
    public double MatchScale { get; init; } = 1d;
    public double TemplateMargin { get; set; }
    public double GateSpatialResidualPixels { get; set; } = double.PositiveInfinity;
    public GateDetection? AssociatedGate { get; set; }
    public int AssociatedGateIndex { get; set; } = -1;
    public SideEntranceGateAssociationKind GateAssociationKind { get; set; }
    public double StructureScore { get; set; }
    public double RawChamferPixels { get; set; } = double.PositiveInfinity;
    public double StructureCompositeCost { get; set; } = double.PositiveInfinity;
    public double StructureEdgeCoverage { get; set; }
    public double StructureOccupancyCoverage { get; set; }
    public double StructureCandidateMargin { get; set; }
    public double IdentityConfidence { get; set; }
    public SideEntranceCandidateDisposition Disposition { get; set; } =
        SideEntranceCandidateDisposition.NeedsVerification;
    public SideEntranceRejectionReason RejectionReason { get; set; }
    public string RejectionDetail { get; set; } = string.Empty;
}

public sealed class SideEntranceScanResult
{
    public GateDetectionResult GateDetection { get; init; } = new();
    public IReadOnlyList<SideEntranceScanCandidate> Candidates { get; init; } = [];
    public string FailureReason { get; init; } = string.Empty;
    public int EligibleMapCount { get; init; }
    public int ReadyMapCount { get; init; }
    public int RejectedCandidateCount { get; init; }
    public IReadOnlyList<SideEntranceScanCandidate> ReliableCandidates =>
        Candidates.Where(candidate => candidate.Disposition ==
            SideEntranceCandidateDisposition.Reliable).ToArray();
    public IReadOnlyList<SideEntranceScanCandidate> ReferenceCandidates =>
        Candidates.Where(candidate => candidate.Disposition ==
            SideEntranceCandidateDisposition.NeedsVerification).ToArray();
    public GateDetection? Gate => GateDetection.Gates
        .OrderByDescending(gate => gate.Score)
        .FirstOrDefault();
}

/// <summary>Immutable selected-map identity carried through side alignment.</summary>
public readonly record struct SideEntranceMapSelection(Guid MapId, string FloorKey)
{
    public bool IsValid => MapId != Guid.Empty && !string.IsNullOrWhiteSpace(FloorKey);
    public bool Matches(SideEntranceScanCandidate? candidate) =>
        IsValid && candidate is not null && candidate.Map.Id == MapId
        && string.Equals(candidate.FloorKey, FloorKey, StringComparison.Ordinal);
    public bool Matches(MapAlignmentSession? seed) =>
        IsValid && seed is not null && seed.MapId == MapId
        && string.Equals(seed.FloorKey, FloorKey, StringComparison.Ordinal);
    public bool Matches(Guid recognitionMapId, Guid resultMapId, string? resultFloor) =>
        IsValid && recognitionMapId == MapId && resultMapId == MapId
        && string.Equals(resultFloor, FloorKey, StringComparison.Ordinal);
    public bool Matches(
        SideEntranceScanCandidate? candidate,
        MapAlignmentSession? seed,
        Guid recognitionMapId,
        Guid resultMapId,
        string? resultFloor) =>
        Matches(candidate) && Matches(seed)
        && Matches(recognitionMapId, resultMapId, resultFloor);
}
