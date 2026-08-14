namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

internal enum AdaptiveScaleState
{
    Provisional,
    Stable,
    Challenged,
    Recovering
}

internal enum AdaptiveScaleReliability
{
    Provisional,
    Reliable,
    Recovering
}

internal enum AdaptiveScaleReliabilityReason
{
    None,
    InitialFiveStreak,
    StructureConsensus,
    TrustedCalibration,
    Disabled
}

internal enum AdaptiveScaleObservationSource
{
    Structure,
    Vpsg
}

internal enum AdaptiveScaleSeedSource
{
    Calibration,
    Runtime
}

internal readonly record struct AdaptiveScaleKey(
    Guid MapId,
    long MapUpdatedAtTicks,
    string FloorKey,
    int ClientWidth,
    int ClientHeight,
    int ViewportWidth,
    int ViewportHeight)
{
    public static AdaptiveScaleKey Create(
        MapRecord map,
        string floorKey,
        MapScreenRect client,
        MapScreenRect viewport) =>
        new(
            map.Id,
            map.UpdatedAt.UtcTicks,
            NormalizeFloor(floorKey),
            (int)Math.Round(client.Width),
            (int)Math.Round(client.Height),
            (int)Math.Round(viewport.Width),
            (int)Math.Round(viewport.Height));

    public bool Matches(MapRecord map, string floorKey) =>
        MapId == map.Id
        && MapUpdatedAtTicks == map.UpdatedAt.UtcTicks
        && string.Equals(FloorKey, NormalizeFloor(floorKey), StringComparison.Ordinal);

    public static string NormalizeFloor(string floorKey) =>
        string.IsNullOrWhiteSpace(floorKey)
            ? "1f"
            : floorKey.Trim().ToLowerInvariant();
}

internal sealed record AdaptiveScaleObservation(
    long FrameId,
    DateTimeOffset ObservedAt,
    double Scale,
    double Confidence,
    double CandidateMargin,
    AdaptiveScaleObservationSource Source,
    MapOverlayTransform Transform);

internal sealed record AdaptiveScaleSeedDecision(
    double Scale,
    double Confidence,
    double RelativeMad,
    AdaptiveScaleSeedSource Source);

internal sealed record AdaptiveVpsgEvidence(
    bool Validated,
    double Scale,
    double Confidence,
    int UniqueMatches,
    int PairVotes,
    double ResidualPixels,
    double RelativeMad);

internal sealed record AdaptiveScaleInitialEvidence(
    long FrameId,
    double RequiredCandidateMargin,
    bool StructureValidated,
    AdaptiveVpsgEvidence? Vpsg = null);

internal sealed record AdaptiveScaleConsensus(
    double Scale,
    double MedianConfidence,
    double RelativeMad,
    double ClusterRange,
    int StructureCount,
    int VpsgCount,
    AdaptiveScaleObservation LatestObservation,
    bool IsProvisionalRecovery = false);

internal sealed record AdaptiveAlignmentDecision(
    RuntimeMapRecognition RecognitionToRender,
    AdaptiveScaleReliability Reliability,
    bool AllowLegacyCacheWrite,
    bool AllowReliableSession,
    bool AllowHotStartMemory,
    bool StartOrbTracking,
    string Status,
    int ConsecutiveHighQualityCount,
    int RequiredHighQualityCount,
    AdaptiveScaleReliabilityReason ReliabilityReason);

internal sealed record AdaptiveOrbDecision(
    MapOverlayTransform Transform,
    bool RequestStructureProbe,
    bool Reanchor,
    AdaptiveScaleState State);

internal sealed record AdaptiveStructureDecision(
    RuntimeMapRecognition Recognition,
    bool ScaleChanged,
    bool BecameReliable,
    bool Reanchor,
    AdaptiveScaleState State,
    AdaptiveScaleConsensus? PendingConsensus = null);

internal sealed class AdaptiveScaleOptions
{
    public bool Enabled { get; set; } = true;
    public double ReliableConfidence { get; set; } = 0.82d;
    public double VpsgConfidence { get; set; } = 0.85d;
    public double StrongRepairConfidence { get; set; } = 0.90d;
    public double Deadband { get; set; } = 0.003d;
    public double ChallengeThreshold { get; set; } = 0.005d;
    public double ConsensusRelativeMad { get; set; } = 0.0025d;
    public double ConsensusClusterRange { get; set; } = 0.005d;
    public double FastConsensusRange { get; set; } = 0.003d;
    public int ObservationWindowMilliseconds { get; set; } = 5000;
    public int MaximumObservations { get; set; } = 7;
    public int MinimumObservationSpacingMilliseconds { get; set; } = 200;
    public int ActiveProbeMilliseconds { get; set; } = 250;
    public int StableProbeMilliseconds { get; set; } = 1500;
    public int ChallengeTimeoutMilliseconds { get; set; } = 2000;
    public int RequiredConsecutiveInitialResults { get; set; } = 5;
    public double InitialScaleClusterTolerance { get; set; } = 0.002d;
    public double RecoveryConfidence { get; set; } = 0.65d;
    public int RecoveryStructureCount { get; set; } = 2;

    public void Normalize()
    {
        ReliableConfidence = Math.Clamp(ReliableConfidence, 0.5d, 1d);
        VpsgConfidence = Math.Clamp(VpsgConfidence, ReliableConfidence, 1d);
        StrongRepairConfidence = Math.Clamp(StrongRepairConfidence, VpsgConfidence, 1d);
        Deadband = Math.Clamp(Deadband, 0.0001d, 0.02d);
        ChallengeThreshold = Math.Max(Deadband, Math.Clamp(ChallengeThreshold, 0.0001d, 0.05d));
        ConsensusRelativeMad = Math.Clamp(ConsensusRelativeMad, 0.0001d, 0.02d);
        ConsensusClusterRange = Math.Clamp(ConsensusClusterRange, ConsensusRelativeMad, 0.03d);
        FastConsensusRange = Math.Clamp(FastConsensusRange, 0.0001d, ConsensusClusterRange);
        ObservationWindowMilliseconds = Math.Clamp(ObservationWindowMilliseconds, 1000, 30000);
        MaximumObservations = Math.Clamp(MaximumObservations, 3, 21);
        MinimumObservationSpacingMilliseconds = Math.Clamp(MinimumObservationSpacingMilliseconds, 50, 2000);
        ActiveProbeMilliseconds = Math.Clamp(ActiveProbeMilliseconds, 100, 2000);
        StableProbeMilliseconds = Math.Clamp(StableProbeMilliseconds, ActiveProbeMilliseconds, 10000);
        ChallengeTimeoutMilliseconds = Math.Clamp(ChallengeTimeoutMilliseconds, 500, 10000);
        RequiredConsecutiveInitialResults = 5;
        InitialScaleClusterTolerance = Math.Clamp(
            InitialScaleClusterTolerance,
            0.0001d,
            0.02d);
        RecoveryConfidence = Math.Clamp(
            RecoveryConfidence,
            0.5d,
            ReliableConfidence);
        RecoveryStructureCount = Math.Clamp(RecoveryStructureCount, 2, 5);
    }
}
