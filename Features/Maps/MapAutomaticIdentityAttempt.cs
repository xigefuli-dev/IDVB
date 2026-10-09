namespace IDVBuff.Features.Maps;

public enum MapAutomaticIdentityStatus
{
    Accepted,
    Ambiguous,
    InsufficientEvidence,
    ResourcesPending,
    TimedOut,
    Cancelled
}

public enum MapAutomaticIdentityCandidateStatus
{
    NotChecked,
    MissingFloor,
    Unsupported,
    ResourcesPending,
    Ready,
    InsufficientEvidence,
    StrictValidationRejected,
    LocalPoseAmbiguous,
    HighSupportUnresolved,
    StrictlyVerified,
    DominatedByObservedStructure,
    TimedOut,
    Cancelled
}

/// <summary>Per-map-floor evidence retained by one complete automatic identity attempt.</summary>
public sealed record MapAutomaticIdentityCandidateDiagnostic
{
    public Guid MapId { get; init; }
    public int SequenceNumber { get; init; }
    public string MapName { get; init; } = string.Empty;
    public string FloorKey { get; init; } = string.Empty;
    public MapAutomaticIdentityCandidateStatus Status { get; init; }
    public bool VpsgAttempted { get; init; }
    public bool VpsgAccepted { get; init; }
    public bool HasCrediblePoseProposal { get; init; }
    public bool HasHighPhysicalSupport { get; init; }
    public bool GeometryAttempted { get; init; }
    public bool IndexedDomainAttempted { get; init; }
    public bool IndexedDomainComparisonComplete { get; init; }
    public bool IndexedDomainClassificationComplete { get; init; }
    public string IndexedDomainClassificationOutcome { get; init; } = string.Empty;
    public bool IndexedDomainPossibleLeafObserved { get; init; }
    public long IndexedDomainsVisited { get; init; }
    /// <summary>Exact count only after full enumeration; null when not enumerated.</summary>
    public int? IndexedDomainsRemaining { get; init; }
    public bool GeometryComparisonComplete { get; init; }
    public int GeometryQueryCorners { get; init; }
    public int GeometryReferenceCorners { get; init; }
    public int GeometryPoseCount { get; init; }
    public int GeometryMatchedCorners { get; init; }
    public double? GeometryWeightedScore { get; init; }
    public int SupportedObservedPointCount { get; init; }
    public Guid? DominatedByMapId { get; init; }
    public string? DominatedByFloorKey { get; init; }
    public double? VpsgConfidence { get; init; }
    public double? VpsgApertureMargin { get; init; }
    public double? VpsgVerificationScore { get; init; }
    public double? VpsgSpatialScore { get; init; }
    public double? ProposalScale { get; init; }
    public double? ProposalOffsetX { get; init; }
    public double? ProposalOffsetY { get; init; }
    public bool StrictValidationAttempted { get; init; }
    public bool StrictValidationAccepted { get; init; }
    public bool BlocksAcceptance { get; init; }
    public MapStructureRejectionReason? StrictRejectionReason { get; init; }
    public string VpsgFailureReason { get; init; } = string.Empty;
    public string FailureReason { get; init; } = string.Empty;
    public MapRecognitionAttempt? AlignmentAttempt { get; init; }
}

/// <summary>
/// Result of a full automatic map-and-floor competition. It carries the exact
/// strict alignment attempt so callers can commit its pose without rerunning it.
/// </summary>
public sealed class MapAutomaticIdentityAttempt
{
    public MapAutomaticIdentityStatus Status { get; init; } =
        MapAutomaticIdentityStatus.InsufficientEvidence;

    public bool Accepted => Status == MapAutomaticIdentityStatus.Accepted;

    public MapRecognitionAttempt? AlignmentAttempt { get; init; }

    public RuntimeMapRecognition? Recognition => AlignmentAttempt?.Recognition;

    /// <summary>
    /// The configured variant identity confirmed by this attempt. The selected
    /// recognition still owns one strictly verified member's actual transform.
    /// Null denotes an ungrouped singleton or an unaccepted attempt.
    /// </summary>
    public Guid? ConfirmedVariantGroupId { get; init; }

    /// <summary>Unique verified member; null while other group members remain possible.</summary>
    public Guid? ConfirmedVariantMemberId { get; init; }

    /// <summary>Configured members ordered by sequence then ID; empty for a singleton.</summary>
    public IReadOnlyList<Guid> ConfirmedVariantMapIds { get; init; } = [];

    public MapCatalogRevision CatalogRevision { get; init; }

    public long CaptureSystemRelativeTicks { get; init; }

    public string? MapClass { get; init; }

    public string? RequestedFloorKey { get; init; }

    /// <summary>Number of map-floor entries in the complete eligible pool.</summary>
    public int CandidateCount { get; init; }

    /// <summary>Number of map-floor entries whose VPSG3 comparison completed.</summary>
    public int ComparedCount { get; init; }

    public string FailureReason { get; init; } = string.Empty;

    public MapScanDiagnostics Diagnostics { get; init; } = new();

    public IReadOnlyList<MapAutomaticIdentityCandidateDiagnostic> CandidateDiagnostics
        { get; init; } = [];

    public IReadOnlyList<MapAutomaticIdentityCandidateDiagnostic> Candidates =>
        CandidateDiagnostics;
}
