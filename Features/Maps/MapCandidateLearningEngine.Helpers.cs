using TorchSharp;
using static TorchSharp.torch;

namespace IDVBuff.Features.Maps;

public sealed partial class MapCandidateLearningEngine
{
    private SiameseMapNetwork CreateNetworkWithFallback()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("IDVB_FORCE_CPU"),
                "1", StringComparison.Ordinal))
        {
            try
            {
                if (torch.cuda.is_available())
                {
                    var network = new SiameseMapNetwork(torch.CUDA);
                    _computeDevice = torch.CUDA;
                    _computeFallbackReason = string.Empty;
                    return network;
                }
                _computeFallbackReason =
                    "CUDA 运行时不可用，已自动使用 CPU。";
            }
            catch (Exception exception)
            {
                _computeFallbackReason =
                    $"CUDA 初始化失败，已自动使用 CPU：{exception.Message}";
            }
        }
        else
        {
            _computeFallbackReason = "已通过 IDVB_FORCE_CPU 强制使用 CPU。";
        }
        _computeDevice = torch.CPU;
        return new SiameseMapNetwork(torch.CPU);
    }

    private static string FormatDevice(Device device) =>
        device.type == DeviceType.CUDA
            ? $"CUDA:{Math.Max(0, device.index)}"
            : "CPU";

    internal static IReadOnlyList<MapLearningSampleManifest> ApplyLatestMatchCorrections(
        IReadOnlyList<MapLearningSampleManifest> samples)
    {
        return samples
            .GroupBy(item => item.MatchId)
            .Select(group => group
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.SampleId, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.CreatedAt)
            .ToArray();
    }

    private static bool IsPromotionMessage(string value) =>
        value.Contains("晋级", StringComparison.Ordinal);

    private static string BuildPromotionBlockReason(
        int sampleCount,
        int distinctMapCount,
        int validationCount,
        SpatialEvaluationMetrics evaluation,
        double traditionalAccuracy,
        MapModelManifest? parent,
        SpatialEvaluationMetrics? parentMetrics,
        bool improvedOverParent)
    {
        var reasons = new List<string>();
        if (sampleCount < 20)
            reasons.Add($"人工对局 {sampleCount}/20");
        if (distinctMapCount < 3)
            reasons.Add($"地图身份 {distinctMapCount}/3");
        if (validationCount < 4)
        {
            reasons.Add($"独立验证对局 {validationCount}/4；样本不足，暂不采用准确率判定");
        }
        else
        {
            if (evaluation.Accuracy < 0.95d)
                reasons.Add($"验证 Top-1 {evaluation.Accuracy:P1}/95.0%");
            if (evaluation.Accuracy <= traditionalAccuracy + 0.01d)
            {
                reasons.Add($"未显著超过传统排序：空间模型 {evaluation.Accuracy:P1}"
                    + $"，传统算法 {traditionalAccuracy:P1}");
            }
            if (evaluation.CalibrationError > 0.10d)
                reasons.Add($"校准误差 {evaluation.CalibrationError:F3}/0.100");
        }
        if (evaluation.TrustedSpatialCount < 4)
        {
            reasons.Add($"可信区域验证 {evaluation.TrustedSpatialCount}/4；"
                + "不能把只会猜地图的模型晋级");
        }
        else
        {
            if (evaluation.SpatialAccuracy < 0.75d)
                reasons.Add($"区域命中率 {evaluation.SpatialAccuracy:P1}/75.0%");
            if (evaluation.SpatialMeanError > 0.20d)
                reasons.Add($"区域平均误差 {evaluation.SpatialMeanError:F3}/0.200");
        }
        if (parent is not null && !improvedOverParent)
        {
            reasons.Add("同一当前验证集上未超过父模型"
                + $"（候选 {evaluation.Accuracy:P1}/"
                + $"{evaluation.CalibrationError:F3}，"
                + $"父模型 {parentMetrics?.Accuracy:P1}/"
                + $"{parentMetrics?.CalibrationError:F3}）");
        }
        return reasons.Count == 0
            ? string.Empty
            : "模型已训练并保留为实验版本；尚未晋级：" + string.Join("；", reasons) + "。";
    }

    internal static bool IsBetterOnSameValidationSet(
        double candidateAccuracy,
        double candidateCalibration,
        double parentAccuracy,
        double parentCalibration)
    {
        const double epsilon = 0.000001d;
        return candidateAccuracy > parentAccuracy + epsilon
            || Math.Abs(candidateAccuracy - parentAccuracy) <= epsilon
                && candidateCalibration < parentCalibration - epsilon;
    }

    internal static bool MeetsSpatialQualification(
        int trustedCount,
        double accuracy,
        double meanError) => trustedCount >= 4
            && accuracy >= 0.75d
            && meanError <= 0.20d;

    internal static bool SpatialMetricsDoNotRegress(
        int trustedCount,
        double candidateAccuracy,
        double candidateMeanError,
        double parentAccuracy,
        double parentMeanError)
    {
        const double epsilon = 0.000001d;
        return trustedCount == 0
            || candidateAccuracy >= parentAccuracy - epsilon
                && candidateMeanError <= parentMeanError + epsilon;
    }

    private static bool IsBetterOnSameValidationSet(
        SpatialEvaluationMetrics candidate,
        SpatialEvaluationMetrics parent)
    {
        const double epsilon = 0.000001d;
        var spatialDoesNotRegress = SpatialMetricsDoNotRegress(
            candidate.TrustedSpatialCount,
            candidate.SpatialAccuracy,
            candidate.SpatialMeanError,
            parent.SpatialAccuracy,
            parent.SpatialMeanError);
        if (!spatialDoesNotRegress)
            return false;
        if (IsBetterOnSameValidationSet(candidate.Accuracy,
                candidate.CalibrationError,
                parent.Accuracy,
                parent.CalibrationError))
            return true;
        return Math.Abs(candidate.Accuracy - parent.Accuracy) <= epsilon
            && Math.Abs(candidate.CalibrationError
                - parent.CalibrationError) <= epsilon
            && (candidate.SpatialAccuracy > parent.SpatialAccuracy + epsilon
                || Math.Abs(candidate.SpatialAccuracy
                    - parent.SpatialAccuracy) <= epsilon
                    && candidate.SpatialMeanError
                        < parent.SpatialMeanError - epsilon);
    }

    private string CreateReferenceEmbeddingKey(MapRecognitionChoice choice) =>
        $"{MapLearningPreprocessor.Version}|{_activeVersion}|"
        + $"{choice.Recognition.Map.Id:D}|"
        + $"{choice.Recognition.Map.ContentVersion}|"
        + $"{choice.Recognition.Result.Floor}|"
        + $"{choice.Recognition.FloorImagePath}";

    private void ClearReferenceEmbeddings()
    {
        foreach (var embedding in _referenceEmbeddings.Values)
            embedding.Dispose();
        _referenceEmbeddings.Clear();
    }

    private static IReadOnlyList<MapRecognitionChoice> AddTraditionalScores(
        IReadOnlyList<MapRecognitionChoice> choices)
    {
        var recognized = choices.Where(choice => !choice.IsReferenceOnly).ToArray();
        var denominator = Math.Max(1, recognized.Length - 1);
        var ranks = recognized.Select((choice, index) => (choice, index))
            .ToDictionary(item => item.choice.Recognition.Map.Id,
                item => 1d - (double)item.index / denominator);
        return choices.Select(choice => CloneChoice(choice,
            traditionalScore: ranks.TryGetValue(choice.Recognition.Map.Id,
                out var score) ? score : 0d)).ToArray();
    }

    private static MapRecognitionChoice CloneChoice(
        MapRecognitionChoice source,
        double? traditionalScore = null,
        double? modelProbability = null,
        double? fusionScore = null,
        string? modelVersion = null,
        string? modelFailure = null,
        double? inferenceMilliseconds = null,
        MapCandidateEvidenceSource? sources = null,
        string? modelMatchedFloorKey = null,
        double? modelMatchedCenterX = null,
        double? modelMatchedCenterY = null,
        double? modelMatchedExtent = null) => new()
    {
        Recognition = source.Recognition,
        VectorError = source.VectorError,
        EvidenceScore = source.EvidenceScore,
        IsReferenceOnly = source.IsReferenceOnly,
        EvidenceLabel = source.EvidenceLabel,
        PreferredOrder = source.PreferredOrder,
        TraditionalScore = traditionalScore ?? source.TraditionalScore,
        ModelProbability = modelProbability,
        FusionScore = fusionScore,
        ModelMatchedFloorKey = modelMatchedFloorKey
            ?? source.ModelMatchedFloorKey,
        ModelMatchedCenterX = modelMatchedCenterX
            ?? source.ModelMatchedCenterX,
        ModelMatchedCenterY = modelMatchedCenterY
            ?? source.ModelMatchedCenterY,
        ModelMatchedExtent = modelMatchedExtent
            ?? source.ModelMatchedExtent,
        ModelVersion = modelVersion ?? source.ModelVersion,
        ModelFailureReason = modelFailure ?? source.ModelFailureReason,
        ModelInferenceMilliseconds = inferenceMilliseconds
            ?? source.ModelInferenceMilliseconds,
        EvidenceSources = sources ?? source.EvidenceSources,
        ModelConfidenceBand = ResolveBand(modelProbability)
    };

    private static MapLearningConfidenceBand ResolveBand(double? value) =>
        value switch
        {
            null => MapLearningConfidenceBand.Unavailable,
            >= 0.85d => MapLearningConfidenceBand.High,
            >= 0.65d => MapLearningConfidenceBand.Medium,
            _ => MapLearningConfidenceBand.Low
        };

    private MapLearningScoreResult ModelUnavailable(
        IReadOnlyList<MapRecognitionChoice> choices,
        MapCandidateDecisionMode mode,
        string reason)
    {
        var failed = choices.Select(choice => CloneChoice(choice,
            modelFailure: reason,
            modelVersion: _activeVersion)).ToArray();
        return new MapLearningScoreResult
        {
            Choices = failed.OrderByDescending(item => item.TraditionalScore)
                .ThenBy(item => item.PreferredOrder).ToArray(),
            ModelAvailable = false,
            ModelQualified = false,
            ModelVersion = _activeVersion,
            FailureReason = reason,
            FellBackToTraditionalOrdering = true
        };
    }

    private async Task LoadVersionCoreAsync(
        string version,
        CancellationToken cancellationToken)
    {
        if (!await _repository.VerifyModelAsync(version, cancellationToken))
            throw new InvalidDataException($"模型 {version} 校验失败。");
        var manifest = await _repository.LoadModelManifestAsync(version,
            cancellationToken)
            ?? throw new InvalidDataException("模型 manifest 缺失。");
        var replacement = CreateNetworkWithFallback();
        try
        {
            replacement.Load(_repository.GetModelDirectory(version));
        }
        catch
        {
            replacement.Dispose();
            throw;
        }
        _network?.Dispose();
        ClearReferenceEmbeddings();
        _network = replacement;
        _activeVersion = version;
        _activeQualified = manifest.IsQualified;
    }

    private async Task RollBackAfterInferenceFailuresAsync(
        CancellationToken cancellationToken)
    {
        var fallback = _repository.ReadLastKnownGoodVersion();
        if (fallback is null || fallback == _activeVersion)
            return;
        var reason = $"模型 {_activeVersion} 连续三次推理失败，自动恢复 {fallback}。";
        await _repository.RestoreAsync(fallback, reason, cancellationToken);
        await LoadVersionCoreAsync(fallback, cancellationToken);
        _consecutiveInferenceFailures = 0;
        SetStatus(Status with
        {
            CurrentVersion = fallback,
            IsAvailable = true,
            IsQualified = _activeQualified,
            LastRollbackReason = reason
        });
    }

    private async Task<MapLearningTrainingResult> FinishWithoutTrainingAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        var state = await _repository.LoadStateAsync(cancellationToken);
        await _repository.SaveStateAsync(state with
        {
            LastTrainingTime = DateTimeOffset.UtcNow,
            LastFailureReason = reason
        }, cancellationToken);
        SetStatus(Status with
        {
            IsTraining = false,
            TrainingPhase = MapLearningTrainingPhase.Failed,
            TrainingProgressCurrent = 0,
            TrainingProgressTotal = 0,
            LastTrainingTime = DateTimeOffset.UtcNow,
            LastFailureReason = reason
        });
        return new MapLearningTrainingResult { Reason = reason };
    }

    private async Task RunQueuedTrainingAsync()
    {
        try
        {
            await TrainNowAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref _queuedTraining, 0);
        }
    }

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
        var retained = ApplyLatestMatchCorrections(
            await _repository.LoadSamplesAsync(cancellationToken));
        var compatible = retained.Where(IsSpatialSample).ToArray();
        var partition = PartitionSpatialSamples(compatible);
        SetStatus(Status with
        {
            HumanSelectionCount = compatible.Length,
            LegacyHumanSelectionCount = retained.Count - compatible.Length,
            MigratedLegacyHumanSelectionCount = compatible.LongCount(item =>
                item.MigratedFromSchemaVersion > 0),
            DistinctMapCount = compatible.Select(item => item.SelectedMapId)
                .Distinct().Count(),
            ValidationMatchCount = partition.Validation.Count
        });
    }

    private void SetStatus(MapLearningStatus status) =>
        Volatile.Write(ref _status, status);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        if (_queuedTrainingTask is not null)
        {
            try { await _queuedTrainingTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        await _modelGate.WaitAsync();
        try
        {
            _network?.Dispose();
            _network = null;
            ClearReferenceEmbeddings();
        }
        finally
        {
            _modelGate.Release();
            _modelGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
