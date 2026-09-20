using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed class MapSampleProviderEngine : IMapCandidateLearningEngine
{
    private const string ExternalModelMessage =
        "主程序仅收集和传输训练样本；模型训练与推理由用户自行接入外部组件。";
    private readonly MapLearningRepository _repository;
    private MapLearningStatus _status = CreateEmptyStatus();

    public MapSampleProviderEngine(string? repositoryRoot = null)
    {
        _repository = new MapLearningRepository(repositoryRoot);
    }

    public bool SupportsTraining => false;
    public MapLearningStatus Status => Volatile.Read(ref _status);
    public string RepositoryRoot => _repository.RootDirectory;

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        _repository.EnsureCreated();
        await _repository.MigrateLegacySamplesAsync(cancellationToken);
        await RefreshStatusAsync(cancellationToken);
    }

    public Task InvalidateReferenceCacheAsync(
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<MapLearningScoreResult> ScoreAsync(
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        MapCandidateDecisionMode mode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new MapLearningScoreResult
        {
            Choices = choices.ToArray(),
            FailureReason = ExternalModelMessage,
            FellBackToTraditionalOrdering = true
        });
    }

    public async Task RecordHumanSelectionAsync(
        Guid matchId,
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        Guid selectedMapId,
        CancellationToken cancellationToken = default,
        MapScreenRect? viewportBounds = null)
    {
        await _repository.SaveHumanSelectionAsync(
            matchId, liveViewport, choices, selectedMapId,
            cancellationToken, viewportBounds);
        await RefreshStatusAsync(cancellationToken);
    }

    public void QueueTraining()
    {
    }

    public Task<MapLearningTrainingResult> TrainNowAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MapLearningTrainingResult
        {
            Reason = ExternalModelMessage
        });

    public async Task<string> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        await _repository.ExportAsync(destinationPath, cancellationToken);
        return Path.GetFullPath(destinationPath);
    }

    public async Task ClearTrainingSamplesAsync(
        CancellationToken cancellationToken = default)
    {
        await _repository.ClearSamplesAsync(cancellationToken);
        await RefreshStatusAsync(cancellationToken);
    }

    public Task<IReadOnlyList<MapModelVersionInfo>> GetVersionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MapModelVersionInfo>>([]);

    public Task RestoreVersionAsync(
        string version,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(ExternalModelMessage));

    public Task SetVersionPinnedAsync(
        string version,
        bool pinned,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException(ExternalModelMessage));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var samples = MapLearningSampleRules.LatestPerMatch(
            await _repository.LoadSamplesAsync(cancellationToken));
        var compatible = samples
            .Where(MapLearningSampleRules.IsSpatialSample)
            .ToArray();
        Volatile.Write(ref _status, CreateEmptyStatus() with
        {
            HumanSelectionCount = compatible.Length,
            LegacyHumanSelectionCount = samples.Count - compatible.Length,
            MigratedLegacyHumanSelectionCount = compatible.LongCount(item =>
                item.MigratedFromSchemaVersion > 0),
            DistinctMapCount = compatible.Select(item => item.SelectedMapId)
                .Distinct().Count(),
            ValidationMatchCount = compatible
                .Where(item => string.Equals(item.Split, "validation",
                    StringComparison.OrdinalIgnoreCase))
                .Select(item => item.MatchId)
                .Distinct().Count(),
            TrustedSpatialValidationCount = compatible
                .Where(item => string.Equals(item.Split, "validation",
                    StringComparison.OrdinalIgnoreCase))
                .Count(item => item.Candidates.Any(candidate =>
                    candidate.IsPositive && candidate.HasTrustedSpatialLabel))
        });
    }

    private static MapLearningStatus CreateEmptyStatus() => new()
    {
        ComputeDevice = "样本提供模式",
        ComputeFallbackReason = ExternalModelMessage
    };
}
