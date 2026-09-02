namespace IDVBuff.Features.Maps;

internal static class MapLearningModelContract
{
    internal const string ArchitectureVersion =
        "idvb-spatial-cross-domain-matcher-128-v3";
}

internal sealed record MapLearningSampleManifest
{
    public int SchemaVersion { get; init; } = 2;
    public int MigratedFromSchemaVersion { get; init; }
    public string SampleId { get; init; } = string.Empty;
    public Guid MatchId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string MapClass { get; init; } = string.Empty;
    public Guid SelectedMapId { get; init; }
    public string LiveImageFile { get; init; } = "live.png";
    public string Split { get; init; } = "train";
    public IReadOnlyList<MapLearningCandidateManifest> Candidates { get; init; } = [];
}

internal sealed record MapLearningCandidateManifest
{
    public Guid MapId { get; init; }
    public string FloorKey { get; init; } = string.Empty;
    public string ReferenceHash { get; init; } = string.Empty;
    public string ReferenceFile { get; init; } = string.Empty;
    public string ReferenceScope { get; init; } = "legacy-crop";
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public bool HasTrustedSpatialLabel { get; init; }
    public double SpatialCenterX { get; init; }
    public double SpatialCenterY { get; init; }
    public double? TraditionalScore { get; init; }
    public double? ModelProbability { get; init; }
    public double? FusionScore { get; init; }
    public bool IsPositive { get; init; }
}

internal sealed record MapModelManifest
{
    public int SchemaVersion { get; init; } = 2;
    public string ArchitectureVersion { get; init; } =
        MapLearningModelContract.ArchitectureVersion;
    public string PreprocessingVersion { get; init; } =
        MapLearningPreprocessor.Version;
    public string Version { get; init; } = string.Empty;
    public string ParentVersion { get; init; } = string.Empty;
    public string DatasetRootHash { get; init; } = string.Empty;
    public string WeightsSha256 { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public MapModelVersionState State { get; init; }
    public bool IsPinned { get; init; }
    public int RandomSeed { get; init; } = 260830;
    public int Epochs { get; init; }
    public int BatchSize { get; init; } = 32;
    public double LearningRate { get; init; } = 0.001d;
    public long HumanSelectionCount { get; init; }
    public int DistinctMapCount { get; init; }
    public double ValidationAccuracy { get; init; }
    public double TraditionalValidationAccuracy { get; init; }
    public double CalibrationError { get; init; }
    public int TrustedSpatialValidationCount { get; init; }
    public double SpatialValidationAccuracy { get; init; }
    public double SpatialMeanError { get; init; } = 1d;
    public double? ParentValidationAccuracyOnCurrentSet { get; init; }
    public double? ParentCalibrationErrorOnCurrentSet { get; init; }
    public double? ParentSpatialValidationAccuracyOnCurrentSet { get; init; }
    public double? ParentSpatialMeanErrorOnCurrentSet { get; init; }
    public bool ImprovedOverParent { get; init; }
    public bool ActivatedAsBestExperimental { get; init; }
    public int ValidationMatchCount { get; init; }
    public bool IsQualified { get; init; }
    public string FailureReason { get; init; } = string.Empty;
}

internal sealed record MapLearningStateDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string LastFailureReason { get; init; } = string.Empty;
    public string LastRollbackReason { get; init; } = string.Empty;
    public string LastPromotionBlockReason { get; init; } = string.Empty;
    public DateTimeOffset? LastTrainingTime { get; init; }
}
