using OpenCvSharp;
using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCandidateLearningEngine : IMapCandidateLearningEngine
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _repository.EnsureCreated();
        await _repository.MigrateLegacySamplesAsync(cancellationToken);
        var samples = ApplyLatestMatchCorrections(
            await _repository.LoadSamplesAsync(cancellationToken));
        var state = await _repository.LoadStateAsync(cancellationToken);
        var current = _repository.ReadCurrentVersion();
        var fallback = _repository.ReadLastKnownGoodVersion();
        var version = current;
        var rollbackReason = state.LastRollbackReason;

        if (version is not null
            && !await _repository.VerifyModelAsync(version, cancellationToken))
        {
            rollbackReason = $"模型 {version} 校验失败，已尝试恢复上一稳定版本。";
            version = fallback;
            if (version is not null
                && await _repository.VerifyModelAsync(version, cancellationToken))
            {
                await _repository.RestoreAsync(version, rollbackReason,
                    cancellationToken);
            }
            else
            {
                version = null;
            }
        }

        if (version is null)
            version = await SelectCompatibleParentAsync(cancellationToken);

        if (version is not null)
        {
            await LoadVersionCoreAsync(version, cancellationToken);
            if (_repository.ReadBestExperimentalVersion() is null)
                await _repository.ActivateExperimentalAsync(version,
                    cancellationToken);
        }

        var compatibleSamples = samples.Where(IsSpatialSample).ToArray();
        var partition = PartitionSpatialSamples(compatibleSamples);
        var maps = compatibleSamples.Select(item => item.SelectedMapId)
            .Distinct().Count();
        var humanMatchCount = compatibleSamples.Length;
        var validationCount = partition.Validation.Count;
        var manifest = string.IsNullOrWhiteSpace(_activeVersion)
            ? null
            : await _repository.LoadModelManifestAsync(_activeVersion,
                cancellationToken);
        SetStatus(Status with
        {
            IsAvailable = _network is not null,
            IsQualified = _activeQualified,
            CurrentVersion = _activeVersion,
            LastKnownGoodVersion = fallback ?? string.Empty,
            HumanSelectionCount = humanMatchCount,
            LegacyHumanSelectionCount = samples.Count - humanMatchCount,
            MigratedLegacyHumanSelectionCount = compatibleSamples.LongCount(
                item => item.MigratedFromSchemaVersion > 0),
            DistinctMapCount = maps,
            ValidationMatchCount = validationCount,
            ValidationAccuracy = manifest?.ValidationAccuracy ?? 0d,
            TraditionalValidationAccuracy =
                manifest?.TraditionalValidationAccuracy ?? 0d,
            CalibrationError = manifest?.CalibrationError ?? 0d,
            TrustedSpatialValidationCount =
                manifest?.TrustedSpatialValidationCount ?? 0,
            SpatialValidationAccuracy =
                manifest?.SpatialValidationAccuracy ?? 0d,
            SpatialMeanError = manifest?.SpatialMeanError ?? 1d,
            LastTrainingTime = state.LastTrainingTime,
            LastFailureReason = IsPromotionMessage(state.LastFailureReason)
                ? string.Empty
                : state.LastFailureReason,
            PromotionBlockReason = string.IsNullOrWhiteSpace(
                state.LastPromotionBlockReason)
                    && IsPromotionMessage(state.LastFailureReason)
                ? state.LastFailureReason
                : state.LastPromotionBlockReason,
            LastRollbackReason = rollbackReason,
            ComputeDevice = FormatDevice(_computeDevice),
            ComputeFallbackReason = _computeFallbackReason
        });
    }

    public async Task<MapLearningScoreResult> ScoreAsync(
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        MapCandidateDecisionMode mode,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (choices.Count == 0)
            return new MapLearningScoreResult();
        var traditional = AddTraditionalScores(choices);
        if (mode == MapCandidateDecisionMode.Traditional)
        {
            return new MapLearningScoreResult
            {
                Choices = traditional,
                ModelAvailable = _network is not null,
                ModelQualified = _activeQualified,
                ModelVersion = _activeVersion
            };
        }

        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            if (_network is null)
                return ModelUnavailable(traditional, mode,
                    "还没有可加载的空间匹配模型。");
            var result = await ScoreSpatialCoreAsync(
                liveViewport, traditional, mode, cancellationToken);
            _consecutiveInferenceFailures = result.ModelAvailable
                ? 0 : _consecutiveInferenceFailures + 1;
            if (_consecutiveInferenceFailures >= 3)
                await RollBackAfterInferenceFailuresAsync(cancellationToken);
            return result;
        }
        finally
        {
            _modelGate.Release();
        }
    }

    public async Task RecordHumanSelectionAsync(
        Guid matchId,
        Mat liveViewport,
        IReadOnlyList<MapRecognitionChoice> choices,
        Guid selectedMapId,
        CancellationToken cancellationToken = default,
        MapScreenRect? viewportBounds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _repository.SaveHumanSelectionAsync(matchId, liveViewport, choices,
            selectedMapId, cancellationToken, viewportBounds);
        var samples = ApplyLatestMatchCorrections(
            await _repository.LoadSamplesAsync(cancellationToken));
        var compatibleSamples = samples.Where(IsSpatialSample).ToArray();
        var partition = PartitionSpatialSamples(compatibleSamples);
        SetStatus(Status with
        {
            HumanSelectionCount = compatibleSamples.Length,
            LegacyHumanSelectionCount = samples.Count - compatibleSamples.Length,
            DistinctMapCount = compatibleSamples.Select(item => item.SelectedMapId)
                .Distinct().Count(),
            ValidationMatchCount = partition.Validation.Count
        });
    }

    public void QueueTraining()
    {
        if (_disposed || Interlocked.Exchange(ref _queuedTraining, 1) != 0)
            return;
        _queuedTrainingTask = Task.Run(RunQueuedTrainingAsync);
    }

    public Task<MapLearningTrainingResult> TrainNowAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Task.Run(
            () => TrainNowCoreAsync(cancellationToken),
            cancellationToken);
    }

    private async Task<MapLearningTrainingResult> TrainNowCoreAsync(
        CancellationToken cancellationToken)
    {
        await _modelGate.WaitAsync(cancellationToken);
        try
        {
            SetStatus(Status with
            {
                IsTraining = true,
                TrainingPhase = MapLearningTrainingPhase.PreparingSamples,
                TrainingEpoch = 0,
                TrainingEpochCount = 20,
                TrainingBatch = 0,
                TrainingBatchCount = 0,
                TrainingProgressCurrent = 0,
                TrainingProgressTotal = 0,
                LastFailureReason = string.Empty
            });
            var rawSamples = await _repository.LoadSamplesAsync(cancellationToken);
            var samples = ApplyLatestMatchCorrections(rawSamples);
            if (samples.Count == 0)
                return await FinishWithoutTrainingAsync("尚无人工标注样本。",
                    cancellationToken);
            var compatibleSamples = samples.Where(IsSpatialSample).ToArray();
            if (compatibleSamples.Length == 0)
            {
                return await FinishWithoutTrainingAsync(
                    "现有样本来自旧的整图二分类契约；请用当前版本重新确认地图，生成完整楼层空间样本。",
                    cancellationToken);
            }
            torch.manual_seed(260830);
            using var candidate = CreateNetworkWithFallback();
            var parent = await SelectCompatibleParentAsync(cancellationToken);
            if (parent is not null)
                candidate.Load(_repository.GetModelDirectory(parent!));
            var partition = PartitionSpatialSamples(compatibleSamples);
            var validationSamples = partition.Validation.ToArray();
            var trainingSamples = partition.Training.ToArray();
            var trainingCases = LoadSpatialCases(trainingSamples, augment: true);
            var evaluationSamples = validationSamples.Length > 0
                ? validationSamples : compatibleSamples;
            var evaluationCases = LoadSpatialCases(
                evaluationSamples, augment: false);
            if (trainingCases.Count == 0 || evaluationCases.Count == 0)
            {
                return await FinishWithoutTrainingAsync(
                    "空间训练样本缺少可解码的完整楼层候选。", cancellationToken);
            }
            var humanMatchCount = compatibleSamples.Length;
            var validationMatchCount = validationSamples.Length;
            var parentManifest = parent is null
                ? null
                : await _repository.LoadModelManifestAsync(parent,
                    cancellationToken);
            SpatialEvaluationMetrics? parentMetrics = null;
            if (parent is not null)
            {
                parentMetrics = EvaluateSpatial(candidate, evaluationCases,
                    cancellationToken, (_, _) => { });
            }
            void ReportTrainingProgress(int epoch, int epochCount, int batch,
                int batchCount, long processed, long total) =>
                    SetStatus(Status with
                    {
                        TrainingPhase = MapLearningTrainingPhase.Training,
                        TrainingEpoch = epoch,
                        TrainingEpochCount = epochCount,
                        TrainingBatch = batch,
                        TrainingBatchCount = batchCount,
                        TrainingProgressCurrent = processed,
                        TrainingProgressTotal = total,
                        ComputeDevice = FormatDevice(candidate.Device),
                        ComputeFallbackReason = _computeFallbackReason
                    });
            TrainSpatialCandidate(candidate, trainingCases,
                cancellationToken, ReportTrainingProgress);
            var evaluation = EvaluateSpatial(
                candidate, evaluationCases,
                cancellationToken, (current, total) => SetStatus(Status with
                {
                    TrainingPhase = MapLearningTrainingPhase.Evaluating,
                    TrainingEpoch = 0,
                    TrainingBatch = 0,
                    TrainingProgressCurrent = current,
                    TrainingProgressTotal = total
                }));
            var traditionalAccuracy = EvaluateTraditionalTopOne(
                evaluationSamples);
            var improvedOverParent = parentMetrics is null
                || IsBetterOnSameValidationSet(
                    evaluation, parentMetrics.Value);
            SetStatus(Status with
            {
                TrainingPhase = MapLearningTrainingPhase.Saving,
                TrainingProgressCurrent = 0,
                TrainingProgressTotal = 0
            });
            var datasetHash = await MapLearningRepository.ComputeDatasetHashAsync(
                compatibleSamples, cancellationToken);
            var distinctMaps = compatibleSamples.Select(item => item.SelectedMapId)
                .Distinct().Count();
            var qualified = humanMatchCount >= 20
                && distinctMaps >= 3
                && validationMatchCount >= 4
                && evaluation.Accuracy >= 0.95d
                && evaluation.Accuracy > traditionalAccuracy + 0.01d
                && evaluation.CalibrationError <= 0.10d
                && MeetsSpatialQualification(
                    evaluation.TrustedSpatialCount,
                    evaluation.SpatialAccuracy,
                    evaluation.SpatialMeanError);
            if (qualified && parentManifest?.IsQualified is true
                && !improvedOverParent)
                qualified = false;
            var promotionBlockReason = BuildPromotionBlockReason(
                humanMatchCount,
                distinctMaps,
                validationMatchCount,
                evaluation,
                traditionalAccuracy,
                parentManifest,
                parentMetrics,
                improvedOverParent);

            var activateCandidate = parent is null
                || (parentManifest?.IsQualified is true
                    ? qualified
                    : improvedOverParent);

            var draft = new MapModelManifest
            {
                ParentVersion = parent ?? string.Empty,
                DatasetRootHash = datasetHash,
                CreatedAt = DateTimeOffset.UtcNow,
                State = MapModelVersionState.Candidate,
                Epochs = 20,
                BatchSize = 1,
                HumanSelectionCount = humanMatchCount,
                DistinctMapCount = distinctMaps,
                ValidationAccuracy = evaluation.Accuracy,
                TraditionalValidationAccuracy = traditionalAccuracy,
                CalibrationError = evaluation.CalibrationError,
                TrustedSpatialValidationCount = evaluation.TrustedSpatialCount,
                SpatialValidationAccuracy = evaluation.SpatialAccuracy,
                SpatialMeanError = evaluation.SpatialMeanError,
                ParentValidationAccuracyOnCurrentSet =
                    parentMetrics?.Accuracy,
                ParentCalibrationErrorOnCurrentSet =
                    parentMetrics?.CalibrationError,
                ParentSpatialValidationAccuracyOnCurrentSet =
                    parentMetrics?.SpatialAccuracy,
                ParentSpatialMeanErrorOnCurrentSet =
                    parentMetrics?.SpatialMeanError,
                ImprovedOverParent = improvedOverParent,
                ActivatedAsBestExperimental = activateCandidate,
                ValidationMatchCount = validationMatchCount,
                IsQualified = qualified,
                FailureReason = qualified
                    ? string.Empty
                    : promotionBlockReason
            };
            var committed = await _repository.CommitModelAsync(candidate, draft,
                cancellationToken);
            SetStatus(Status with
            {
                TrainingPhase = MapLearningTrainingPhase.Reloading,
                TrainingProgressCurrent = 0,
                TrainingProgressTotal = 0
            });
            await VerifySpatialReloadConsistencyAsync(candidate, committed,
                evaluationCases[0], cancellationToken);
            if (qualified)
            {
                await _repository.PromoteAsync(committed, cancellationToken);
                await LoadVersionCoreAsync(committed.Version, cancellationToken);
            }
            else if (!activateCandidate && parent is not null
                && await _repository.VerifyModelAsync(parent, cancellationToken))
            {
                await LoadVersionCoreAsync(parent, cancellationToken);
            }
            else
            {
                await _repository.ActivateExperimentalAsync(
                    committed.Version, cancellationToken);
                await LoadVersionCoreAsync(committed.Version, cancellationToken);
            }
            var activeManifest = string.IsNullOrWhiteSpace(_activeVersion)
                ? null
                : await _repository.LoadModelManifestAsync(_activeVersion,
                    cancellationToken);
            await _repository.PruneModelHistoryAsync(cancellationToken);
            var state = await _repository.LoadStateAsync(cancellationToken);
            state = state with
            {
                LastTrainingTime = DateTimeOffset.UtcNow,
                LastFailureReason = string.Empty,
                LastPromotionBlockReason = committed.FailureReason,
                LastRollbackReason = parent is null && activateCandidate
                    ? string.Empty
                    : state.LastRollbackReason
            };
            await _repository.SaveStateAsync(state, cancellationToken);
            SetStatus(Status with
            {
                IsAvailable = true,
                IsQualified = _activeQualified,
                IsTraining = false,
                TrainingPhase = MapLearningTrainingPhase.Completed,
                TrainingProgressCurrent = 1,
                TrainingProgressTotal = 1,
                ComputeDevice = FormatDevice(_computeDevice),
                LastTrainingComputeDevice = FormatDevice(candidate.Device),
                ComputeFallbackReason = _computeFallbackReason,
                CurrentVersion = _activeVersion,
                LastKnownGoodVersion = qualified
                    ? committed.Version
                    : Status.LastKnownGoodVersion,
                HumanSelectionCount = humanMatchCount,
                DistinctMapCount = distinctMaps,
                ValidationAccuracy = activeManifest?.ValidationAccuracy ?? 0d,
                TraditionalValidationAccuracy =
                    activeManifest?.TraditionalValidationAccuracy ?? 0d,
                CalibrationError = activeManifest?.CalibrationError ?? 0d,
                TrustedSpatialValidationCount =
                    activeManifest?.TrustedSpatialValidationCount ?? 0,
                SpatialValidationAccuracy =
                    activeManifest?.SpatialValidationAccuracy ?? 0d,
                SpatialMeanError = activeManifest?.SpatialMeanError ?? 1d,
                ValidationMatchCount = validationMatchCount,
                LastTrainingTime = state.LastTrainingTime,
                LastFailureReason = string.Empty,
                PromotionBlockReason = committed.FailureReason
            });
            return new MapLearningTrainingResult
            {
                Trained = true,
                Promoted = qualified,
                Activated = activateCandidate,
                ImprovedOverParent = improvedOverParent,
                Version = committed.Version,
                ActiveVersion = _activeVersion,
                ParentVersion = parent ?? string.Empty,
                ValidationAccuracy = evaluation.Accuracy,
                TraditionalValidationAccuracy = traditionalAccuracy,
                CalibrationError = evaluation.CalibrationError,
                TrustedSpatialValidationCount = evaluation.TrustedSpatialCount,
                SpatialValidationAccuracy = evaluation.SpatialAccuracy,
                SpatialMeanError = evaluation.SpatialMeanError,
                ValidationMatchCount = validationMatchCount,
                Reason = committed.FailureReason
            };
        }
        catch (Exception exception)
        {
            var state = await _repository.LoadStateAsync(CancellationToken.None);
            await _repository.SaveStateAsync(state with
            {
                LastTrainingTime = DateTimeOffset.UtcNow,
                LastFailureReason = exception.Message
            }, CancellationToken.None);
            SetStatus(Status with
            {
                IsTraining = false,
                TrainingPhase = MapLearningTrainingPhase.Failed,
                TrainingProgressCurrent = 0,
                TrainingProgressTotal = 0,
                LastFailureReason = exception.Message,
                LastTrainingTime = DateTimeOffset.UtcNow
            });
            return new MapLearningTrainingResult
            {
                Reason = exception.Message
            };
        }
        finally
        {
            _modelGate.Release();
        }
    }

}
