namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private sealed record AutomaticIdentityFloorInput(
        MapRecord Map,
        string FloorKey,
        bool HasFloorDefinition,
        string FailureReason);

    private sealed record AutomaticGeometryPoseValidation(
        MapLocalGeometryPose Proposal,
        MapRecognitionAttempt Attempt,
        MapLocalGeometryPose? FinalPose);

    private sealed record AutomaticIdentityPoseEvidence(
        AutomaticIdentityFloorWork Owner,
        MapRecognitionAttempt Attempt,
        MapLocalGeometryPose Pose,
        bool CanAccept);

    private sealed class AutomaticIdentityFloorWork(AutomaticIdentityFloorInput input)
    {
        public MapRecord Map { get; } = input.Map;
        public string FloorKey { get; } = input.FloorKey;
        public string InitialFailureReason { get; } = input.FailureReason;
        public bool HasFloorDefinition { get; } = input.HasFloorDefinition;
        public Vpsg3FloorIndexLease? Lease { get; set; }
        public MapAutomaticIdentityCandidateStatus Status { get; set; } =
            MapAutomaticIdentityCandidateStatus.NotChecked;
        public bool VpsgAttempted { get; set; }
        public bool VpsgAccepted { get; set; }
        public bool NativePoseComparisonComplete { get; set; }
        public bool HasCrediblePoseProposal { get; set; }
        public bool HasHighPhysicalSupport { get; set; }
        public bool GeometryAttempted { get; set; }
        public bool IndexedDomainAttempted { get; set; }
        public bool IndexedDomainComparisonComplete { get; set; }
        public long IndexedDomainsVisited { get; set; }
        public int IndexedDomainsRemaining { get; set; }
        public MapFrontEntryReferenceIndex? IndexedDomainSource { get; set; }
        public IReadOnlyList<OpenCvSharp.Point>? IndexedWallWitnesses { get; set; }
        public bool GeometryComparisonComplete { get; set; }
        public int GeometryQueryCorners { get; set; }
        public int GeometryReferenceCorners { get; set; }
        public int GeometryPoseCount { get; set; }
        public int GeometryMatchedCorners { get; set; }
        public double? GeometryWeightedScore { get; set; }
        public int SupportedObservedPointCount { get; set; }
        public Guid? DominatedByMapId { get; set; }
        public string? DominatedByFloorKey { get; set; }
        public double? VpsgConfidence { get; set; }
        public double? VpsgApertureMargin { get; set; }
        public double? VpsgVerificationScore { get; set; }
        public double? VpsgSpatialScore { get; set; }
        public double? ProposalScale { get; set; }
        public double? ProposalOffsetX { get; set; }
        public double? ProposalOffsetY { get; set; }
        public bool StrictValidationAttempted { get; set; }
        public bool StrictValidationAccepted { get; set; }
        public bool BlocksAcceptance { get; set; }
        public MapStructureRejectionReason? StrictRejectionReason { get; set; }
        public string VpsgFailureReason { get; set; } = string.Empty;
        public string FailureReason { get; set; } = string.Empty;
        public MapRecognitionAttempt? AlignmentAttempt { get; set; }
        public bool RunnerUpValidationAttempted { get; set; }
        public MapRecognitionAttempt? RunnerUpAlignmentAttempt { get; set; }
        public List<AutomaticGeometryPoseValidation> GeometryValidations { get; } = [];
        public IReadOnlyList<MapLocalCornerGeometry>? GeometryReferenceGeometry { get; set; }
        // Owned by one floor in one same-frame run. Never shared across frames,
        // catalog revisions, maps or floors; confidence is an actual input too.
        public Dictionary<(double Scale, double X, double Y, double Confidence),
            MapRecognitionAttempt> StrictPoseResults { get; } = [];
        public int StrictExecutionCount { get; set; }
        public List<MapRecognitionAttempt> StrictExecutedResults { get; } = [];

        public MapAutomaticIdentityCandidateDiagnostic ToDiagnostic() => new()
        {
            MapId = Map.Id,
            SequenceNumber = Map.SequenceNumber,
            MapName = Map.DisplayName,
            FloorKey = FloorKey,
            Status = Status,
            VpsgAttempted = VpsgAttempted,
            VpsgAccepted = VpsgAccepted,
            HasCrediblePoseProposal = HasCrediblePoseProposal,
            HasHighPhysicalSupport = HasHighPhysicalSupport,
            GeometryAttempted = GeometryAttempted,
            IndexedDomainAttempted = IndexedDomainAttempted,
            IndexedDomainComparisonComplete = IndexedDomainComparisonComplete,
            IndexedDomainsVisited = IndexedDomainsVisited,
            IndexedDomainsRemaining = IndexedDomainsRemaining,
            GeometryComparisonComplete = GeometryComparisonComplete,
            GeometryQueryCorners = GeometryQueryCorners,
            GeometryReferenceCorners = GeometryReferenceCorners,
            GeometryPoseCount = GeometryPoseCount,
            GeometryMatchedCorners = GeometryMatchedCorners,
            GeometryWeightedScore = GeometryWeightedScore,
            SupportedObservedPointCount = SupportedObservedPointCount,
            DominatedByMapId = DominatedByMapId,
            DominatedByFloorKey = DominatedByFloorKey,
            VpsgConfidence = VpsgConfidence,
            VpsgApertureMargin = VpsgApertureMargin,
            VpsgVerificationScore = VpsgVerificationScore,
            VpsgSpatialScore = VpsgSpatialScore,
            ProposalScale = ProposalScale,
            ProposalOffsetX = ProposalOffsetX,
            ProposalOffsetY = ProposalOffsetY,
            StrictValidationAttempted = StrictValidationAttempted,
            StrictValidationAccepted = StrictValidationAccepted,
            BlocksAcceptance = BlocksAcceptance,
            StrictRejectionReason = StrictRejectionReason,
            VpsgFailureReason = VpsgFailureReason,
            FailureReason = string.IsNullOrWhiteSpace(FailureReason)
                ? InitialFailureReason
                : FailureReason,
            AlignmentAttempt = AlignmentAttempt
        };
    }
}
