namespace IDVBuff.Features.Maps;

public sealed partial class MapCandidateLearningEngine
{
    public async Task<IReadOnlyList<MapModelVersionInfo>> GetVersionsAsync(
        CancellationToken cancellationToken = default) =>
        (await _repository.LoadModelManifestsAsync(cancellationToken))
        .Select(item => new MapModelVersionInfo
        {
            Version = item.Version,
            ParentVersion = item.ParentVersion,
            CreatedAt = item.CreatedAt,
            State = item.State,
            IsPinned = item.IsPinned,
            HumanSelectionCount = item.HumanSelectionCount,
            DistinctMapCount = item.DistinctMapCount,
            ValidationMatchCount = item.ValidationMatchCount,
            ValidationAccuracy = item.ValidationAccuracy,
            TraditionalValidationAccuracy =
                item.TraditionalValidationAccuracy,
            CalibrationError = item.CalibrationError,
            TrustedSpatialValidationCount =
                item.TrustedSpatialValidationCount,
            SpatialValidationAccuracy = item.SpatialValidationAccuracy,
            SpatialMeanError = item.SpatialMeanError,
            FailureReason = item.FailureReason
        }).ToArray();

    public async Task RestoreVersionAsync(
        string version,
        CancellationToken cancellationToken = default)
    {
        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            await _repository.RestoreAsync(version,
                $"用户手动恢复模型 {version}。", cancellationToken);
            await LoadVersionCoreAsync(version, cancellationToken);
            SetStatus(Status with
            {
                IsAvailable = true,
                IsQualified = _activeQualified,
                CurrentVersion = version,
                LastRollbackReason = $"用户手动恢复模型 {version}。"
            });
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task SetVersionPinnedAsync(
        string version,
        bool pinned,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _repository.LoadModelManifestAsync(
            version, cancellationToken)
            ?? throw new InvalidDataException("模型版本不存在。");
        await _repository.UpdateModelManifestAsync(
            manifest with { IsPinned = pinned }, cancellationToken);
    }
}
