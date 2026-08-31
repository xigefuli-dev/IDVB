using OpenCvSharp;
using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private readonly IMapCandidateLearningEngine _learningEngine;
    private Mat? _lastLearningViewport;
    private MapScreenRect _lastLearningClientBounds;
    private MapScreenRect _lastLearningViewportBounds;
    private IntPtr _lastLearningWindowHandle;
    private string _lastLearningMapClass = string.Empty;
    private bool _hasPendingMapLearningSample;
    private Guid? _lastRecordedMapLearningMapId;
    private MapLearningStatus? _externalMapLearningStatus;
    private int _externalTrainingQueued;
    private string _mapLearningComputeDiagnostic = string.Empty;
    private string _lastTrainingComputeDevice = string.Empty;
    private string _gpuInitializationStatus = string.Empty;

    public string MapLearningGpuStatus => string.IsNullOrWhiteSpace(
        Volatile.Read(ref _gpuInitializationStatus))
            ? MapGpuTrainingSidecar.Diagnose().Message
            : Volatile.Read(ref _gpuInitializationStatus);

    public MapLearningStatus MapLearningStatus =>
        Volatile.Read(ref _externalMapLearningStatus)
        ?? _learningEngine.Status with
        {
            ComputeFallbackReason = string.IsNullOrWhiteSpace(
                Volatile.Read(ref _mapLearningComputeDiagnostic))
                    ? _learningEngine.Status.ComputeFallbackReason
                    : Volatile.Read(ref _mapLearningComputeDiagnostic),
            LastTrainingComputeDevice = string.IsNullOrWhiteSpace(
                Volatile.Read(ref _lastTrainingComputeDevice))
                    ? _learningEngine.Status.LastTrainingComputeDevice
                    : Volatile.Read(ref _lastTrainingComputeDevice)
        };

    public async Task SetCandidateDecisionModeAsync(MapCandidateDecisionMode mode)
    {
        if (_settings!.CandidateDecisionMode == mode)
            return;
        _settings!.CandidateDecisionMode = mode;
        // 已冻结候选包含旧模式的排序/模型证据。切换引擎后必须重新扫描，
        // 不能在开图热键上偷偷重算并重新引入展示延迟。
        ClearPendingBackgroundScan();
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetContinuousMapLearningEnabledAsync(bool enabled)
    {
        _settings!.ContinuousMapLearningEnabled = enabled;
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetAutomaticMapModelTrainingEnabledAsync(bool enabled)
    {
        _settings!.AutomaticMapModelTrainingEnabled = enabled;
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMapLearningOptionsForSession(
        MapCandidateDecisionMode mode,
        bool continuousLearningEnabled)
    {
        if (_settings is null)
            return;
        if (_settings.CandidateDecisionMode != mode)
            ClearPendingBackgroundScan();
        _settings.CandidateDecisionMode = mode;
        _settings.ContinuousMapLearningEnabled = continuousLearningEnabled;
    }

    private async Task InitializeLearningEngineAsync()
    {
        try
        {
            await _learningEngine.InitializeAsync(_lifetimeCts.Token);
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                "地图学习模型初始化失败；传统识别仍可继续使用。",
                details: new()
                {
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
        }
    }

    private void RememberMapLearningContext(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        string mapClass)
    {
        _lastLearningViewport?.Dispose();
        _lastLearningViewport = frame.Image.Clone();
        _lastLearningClientBounds = frame.ClientBounds;
        _lastLearningViewportBounds = frame.ViewportBounds;
        _lastLearningWindowHandle = frame.WindowHandle;
        _lastLearningMapClass = mapClass;
        _lastCandidateChoices = choices;
    }

    private async Task RecordHumanMapSelectionAsync(
        CapturedGameFrame frame,
        IReadOnlyList<MapRecognitionChoice> choices,
        Guid selectedMapId,
        CancellationToken cancellationToken)
    {
        if (_settings?.ContinuousMapLearningEnabled is not true)
            return;

        var matchId = _matchSession.Snapshot.MatchId;
        if (matchId == Guid.Empty)
            return;

        try
        {
            await _learningEngine.RecordHumanSelectionAsync(
                matchId,
                frame.Image,
                choices,
                selectedMapId,
                cancellationToken,
                frame.ViewportBounds);
            _hasPendingMapLearningSample = true;
            _lastRecordedMapLearningMapId = selectedMapId;
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                "人工地图标签保存失败；本局不会用自动结果替代标签。",
                details: new()
                {
                    ["mapId"] = selectedMapId,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
        }
    }

    private async Task FinalizeMapLearningLabelAsync(
        MapMatchSnapshot endingMatch,
        Guid? finalMapId)
    {
        if (!_hasPendingMapLearningSample
            || finalMapId is not { } selectedMapId
            || selectedMapId == _lastRecordedMapLearningMapId
            || _lastLearningViewport is null
            || string.IsNullOrWhiteSpace(_lastLearningMapClass))
        {
            return;
        }
        try
        {
            var choices = await BuildNativeCandidateChoicesAsync(
                _lastCandidateChoices,
                _lastLearningMapClass);
            if (!choices.Any(choice =>
                    choice.Recognition.Map.Id == selectedMapId))
            {
                throw new InvalidDataException(
                    "结束对局时的最终地图变体已不在当前 Class 候选中。");
            }
            await _learningEngine.RecordHumanSelectionAsync(
                endingMatch.MatchId,
                _lastLearningViewport,
                choices,
                selectedMapId,
                _lifetimeCts.Token,
                _lastLearningViewportBounds);
            _lastRecordedMapLearningMapId = selectedMapId;
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"结束对局前按最终地图变体纠正训练标签 · map={selectedMapId}");
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                "最终地图变体训练标签保存失败；不会用自动结果替代。",
                details: new()
                {
                    ["mapId"] = selectedMapId,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
        }
    }

    public async Task CorrectMapAsync()
    {
        if (_headless
            || !_matchSession.Snapshot.IsStarted
            || _lastLearningViewport is null
            || string.IsNullOrWhiteSpace(_lastLearningMapClass))
        {
            _statusMessage = "当前没有可用于纠正的地图识别区域。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var cancellationToken = CurrentMatchCancellationToken;
        var baseChoices = await BuildNativeCandidateChoicesAsync(
            _lastCandidateChoices,
            _lastLearningMapClass);
        var scored = await _learningEngine.ScoreAsync(
            _lastLearningViewport,
            baseChoices,
            _settings?.CandidateDecisionMode
                ?? MapCandidateDecisionMode.Traditional,
            cancellationToken);
        using var frame = new CapturedGameFrame(
            _lastLearningViewport.Clone(),
            _lastLearningClientBounds,
            _lastLearningViewportBounds,
            _lastLearningWindowHandle);
        var decision = await MapManualCandidateWindow.ShowAsync(
            frame,
            scored.Choices,
            "纠正地图：请选择当前 Class 中的正确地图。",
            cancellationToken,
            _captureProtection,
            _mapRepository,
            frame.ViewportBounds);
        if (decision.Kind != MapCandidateDecisionKind.SelectKnownMap
            || decision.CandidateIndex is not { } index
            || index < 0
            || index >= scored.Choices.Count)
        {
            return;
        }

        var choice = scored.Choices[index];
        await RecordHumanMapSelectionAsync(
            frame,
            scored.Choices,
            choice.Recognition.Map.Id,
            cancellationToken);
        UnlockMapForRescan();
        var recognition = MapCvRecognitionService.ConfirmChoice(choice);
        LockSelectedMapIdentity(recognition, frame, userConfirmed: true);
        _statusMessage =
            $"已纠正并锁定地图：{recognition.Map.DisplayName}；旧地图的对齐证据已撤销。";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<MapLearningTrainingResult> TrainMapModelNowAsync(
        CancellationToken cancellationToken = default)
    {
        var diagnostic = MapGpuTrainingSidecar.Diagnose();
        if (!diagnostic.IsPrepared)
        {
            SetExternalLearningDiagnostic(diagnostic.Message);
            return await _learningEngine.TrainNowAsync(cancellationToken);
        }
        try
        {
            Volatile.Write(ref _externalMapLearningStatus,
                _learningEngine.Status with
                {
                    IsTraining = true,
                    TrainingPhase = MapLearningTrainingPhase.PreparingSamples,
                    ComputeDevice = "CUDA sidecar",
                    ComputeFallbackReason = string.Empty
                });
            var result = await MapGpuTrainingSidecar.TrainAsync(
                diagnostic.ExecutablePath,
                _learningEngine.RepositoryRoot,
                status => Volatile.Write(ref _externalMapLearningStatus, status),
                cancellationToken);
            await _learningEngine.InitializeAsync(cancellationToken);
            Volatile.Write(ref _lastTrainingComputeDevice, "CUDA:0 sidecar");
            Volatile.Write(ref _mapLearningComputeDiagnostic, string.Empty);
            return result;
        }
        catch (Exception exception)
        {
            SetExternalLearningDiagnostic(
                $"GPU sidecar 失败，已回退 CPU：{exception.Message}");
            return await _learningEngine.TrainNowAsync(cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _externalMapLearningStatus, null);
        }
    }

    public async Task<MapGpuInitializationResult> InitializeMapLearningGpuAsync(
        CancellationToken cancellationToken = default)
    {
        var diagnostic = MapGpuTrainingSidecar.Diagnose();
        OutputLog.Write("INFO", "MAP/GPU",
            $"GPU initialization requested | prepared={diagnostic.IsPrepared}"
                + $" | sidecar={diagnostic.ExecutablePath}");
        _logCollector.Append(MapLogCategory.System, MapLogLevel.Info,
            "GPU 初始化按钮已触发。", details: new()
            {
                ["sidecarPrepared"] = diagnostic.IsPrepared,
                ["sidecarPath"] = diagnostic.ExecutablePath
            });
        if (!diagnostic.IsPrepared)
        {
            Volatile.Write(ref _gpuInitializationStatus, diagnostic.Message);
            OutputLog.Write("WARNING", "MAP/GPU", diagnostic.Message);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return new MapGpuInitializationResult(false, diagnostic.Message);
        }
        Volatile.Write(ref _gpuInitializationStatus, "正在初始化 CUDA 与 cuDNN…");
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var result = await MapGpuTrainingSidecar.InitializeAsync(
                diagnostic.ExecutablePath, _learningEngine.RepositoryRoot,
                cancellationToken);
            Volatile.Write(ref _gpuInitializationStatus, result.Message);
            var level = result.Succeeded ? MapLogLevel.Info : MapLogLevel.Warning;
            _logCollector.Append(MapLogCategory.System, level, result.Message,
                elapsedMs: result.ElapsedMilliseconds, details: new()
                {
                    ["sidecarPath"] = diagnostic.ExecutablePath,
                    ["sidecarProcessId"] = result.ProcessId,
                    ["sidecarExitCode"] = result.ExitCode,
                    ["succeeded"] = result.Succeeded
                });
            OutputLog.Write(result.Succeeded ? "INFO" : "WARNING", "MAP/GPU",
                $"{result.Message} | pid={result.ProcessId}"
                    + $" | exitCode={result.ExitCode}"
                    + $" | elapsedMs={result.ElapsedMilliseconds:F1}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            const string message = "GPU 初始化已取消，sidecar 已回收。";
            Volatile.Write(ref _gpuInitializationStatus, message);
            OutputLog.Write("WARNING", "MAP/GPU", message);
            return new MapGpuInitializationResult(false, message);
        }
        catch (Exception exception)
        {
            var message = $"GPU 初始化失败：{exception.Message}";
            Volatile.Write(ref _gpuInitializationStatus, message);
            _logCollector.Append(MapLogCategory.System, MapLogLevel.Error,
                message, details: new()
                {
                    ["sidecarPath"] = diagnostic.ExecutablePath,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
            OutputLog.Write("ERROR", "MAP/GPU", message, exception);
            return new MapGpuInitializationResult(false, message);
        }
        finally
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void QueueMapModelTraining()
    {
        if (Interlocked.Exchange(ref _externalTrainingQueued, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await TrainMapModelNowAsync(_lifetimeCts.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _externalTrainingQueued, 0);
            }
        });
    }

    private void SetExternalLearningDiagnostic(string reason)
    {
        Volatile.Write(ref _mapLearningComputeDiagnostic, reason);
    }

    public Task<string> ExportMapTrainingDataAsync(
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        _learningEngine.ExportAsync(destinationPath, cancellationToken);

    public Task ClearMapTrainingSamplesAsync(
        CancellationToken cancellationToken = default) =>
        _learningEngine.ClearTrainingSamplesAsync(cancellationToken);

    public Task<IReadOnlyList<MapModelVersionInfo>> GetMapModelVersionsAsync(
        CancellationToken cancellationToken = default) =>
        _learningEngine.GetVersionsAsync(cancellationToken);

    public Task RestoreMapModelVersionAsync(
        string version,
        CancellationToken cancellationToken = default) =>
        _learningEngine.RestoreVersionAsync(version, cancellationToken);

    public Task SetMapModelVersionPinnedAsync(
        string version,
        bool pinned,
        CancellationToken cancellationToken = default) =>
        _learningEngine.SetVersionPinnedAsync(version, pinned, cancellationToken);

    private void ClearMapLearningContext()
    {
        _lastLearningViewport?.Dispose();
        _lastLearningViewport = null;
        _lastLearningClientBounds = default;
        _lastLearningViewportBounds = default;
        _lastLearningWindowHandle = IntPtr.Zero;
        _lastLearningMapClass = string.Empty;
        _hasPendingMapLearningSample = false;
        _lastRecordedMapLearningMapId = null;
    }
}
