using OpenCvSharp;

namespace IDVBuff.Features.Maps;

[Flags]
public enum MapCandidateEvidenceSource
{
    None = 0,
    Traditional = 1,
    Model = 2
}

public enum MapLearningConfidenceBand
{
    Unavailable,
    Low,
    Medium,
    High
}

public enum MapModelVersionState
{
    Candidate,
    Stable,
    Rejected,
    RolledBack
}

public enum MapLearningTrainingPhase
{
    Idle,
    PreparingSamples,
    Training,
    Evaluating,
    Saving,
    Reloading,
    Completed,
    Failed
}

public sealed record MapLearningStatus
{
    public bool IsAvailable { get; init; }
    public bool IsQualified { get; init; }
    public bool IsTraining { get; init; }
    public MapLearningTrainingPhase TrainingPhase { get; init; }
    public int TrainingEpoch { get; init; }
    public int TrainingEpochCount { get; init; }
    public int TrainingBatch { get; init; }
    public int TrainingBatchCount { get; init; }
    public long TrainingProgressCurrent { get; init; }
    public long TrainingProgressTotal { get; init; }
    public string ComputeDevice { get; init; } = "CPU";
    public string LastTrainingComputeDevice { get; init; } = string.Empty;
    public string ComputeFallbackReason { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string LastKnownGoodVersion { get; init; } = string.Empty;
    public long HumanSelectionCount { get; init; }
    public long LegacyHumanSelectionCount { get; init; }
    public long MigratedLegacyHumanSelectionCount { get; init; }
    public int DistinctMapCount { get; init; }
    public int ValidationMatchCount { get; init; }
    public int RequiredHumanSelectionCount { get; init; } = 20;
    public int RequiredDistinctMapCount { get; init; } = 3;
    public int RequiredValidationMatchCount { get; init; } = 4;
    public double ValidationAccuracy { get; init; }
    public double TraditionalValidationAccuracy { get; init; }
    public double CalibrationError { get; init; }
    public int TrustedSpatialValidationCount { get; init; }
    public int RequiredTrustedSpatialValidationCount { get; init; } = 4;
    public double SpatialValidationAccuracy { get; init; }
    public double SpatialMeanError { get; init; } = 1d;
    public DateTimeOffset? LastTrainingTime { get; init; }
    public string LastFailureReason { get; init; } = string.Empty;
    public string PromotionBlockReason { get; init; } = string.Empty;
    public string LastRollbackReason { get; init; } = string.Empty;
}

public sealed record MapLearningScoreResult
{
    public IReadOnlyList<MapRecognitionChoice> Choices { get; init; } = [];
    public bool ModelAvailable { get; init; }
    public bool ModelQualified { get; init; }
    public string ModelVersion { get; init; } = string.Empty;
    public string FailureReason { get; init; } = string.Empty;
    public bool FellBackToTraditionalOrdering { get; init; }
}

public sealed record MapLearningTrainingResult
{
    public bool Trained { get; init; }
    public bool Promoted { get; init; }
    public bool Activated { get; init; }
    public bool ImprovedOverParent { get; init; }
    public string Version { get; init; } = string.Empty;
    public string ActiveVersion { get; init; } = string.Empty;
    public string ParentVersion { get; init; } = string.Empty;
    public double ValidationAccuracy { get; init; }
    public double TraditionalValidationAccuracy { get; init; }
    public double CalibrationError { get; init; }
    public int TrustedSpatialValidationCount { get; init; }
    public double SpatialValidationAccuracy { get; init; }
    public double SpatialMeanError { get; init; } = 1d;
    public int ValidationMatchCount { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed record MapModelVersionInfo
{
    public string Version { get; init; } = string.Empty;
    public string ParentVersion { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public MapModelVersionState State { get; init; }
    public bool IsPinned { get; init; }
    public long HumanSelectionCount { get; init; }
    public int DistinctMapCount { get; init; }
    public int ValidationMatchCount { get; init; }
    public double ValidationAccuracy { get; init; }
    public double TraditionalValidationAccuracy { get; init; }
    public double CalibrationError { get; init; }
    public int TrustedSpatialValidationCount { get; init; }
    public double SpatialValidationAccuracy { get; init; }
    public double SpatialMeanError { get; init; } = 1d;
    public string FailureReason { get; init; } = string.Empty;
}

public interface IMapCandidateLearningEngine : IAsyncDisposable
{
    bool SupportsTraining { get; }
    MapLearningStatus Status { get; }
    string RepositoryRoot { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task InvalidateReferenceCacheAsync(
        CancellationToken cancellationToken = default);

    Task<MapLearningScoreResult> ScoreAsync(
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        MapCandidateDecisionMode mode,
        CancellationToken cancellationToken = default);

    Task RecordHumanSelectionAsync(
        Guid matchId,
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        Guid selectedMapId,
        CancellationToken cancellationToken = default,
        MapScreenRect? viewportBounds = null);

    void QueueTraining();

    Task<MapLearningTrainingResult> TrainNowAsync(
        CancellationToken cancellationToken = default);

    Task<string> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task ClearTrainingSamplesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MapModelVersionInfo>> GetVersionsAsync(
        CancellationToken cancellationToken = default);

    Task RestoreVersionAsync(
        string version,
        CancellationToken cancellationToken = default);

    Task SetVersionPinnedAsync(
        string version,
        bool pinned,
        CancellationToken cancellationToken = default);
}
