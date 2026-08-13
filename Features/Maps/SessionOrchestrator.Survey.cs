using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public SurveyStatusSnapshot SurveyStatus => _surveyCoordinator.Status;

    public Task ArmSurveyProjectAsync(
        Guid? projectId,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.ArmResumeAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<SurveyProjectSummary>> GetSurveyProjectsAsync(
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.ListProjectsAsync(cancellationToken);

    public Task<SurveyOperationResult<SurveyProjectSnapshot>> DuplicateSurveyProjectAsync(
        Guid projectId,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.DuplicateProjectAsync(projectId, name, cancellationToken);

    public Task<SurveyProjectSnapshot?> GetSurveyProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.GetProjectAsync(projectId, cancellationToken);

    public Task<SurveyOperationResult<SurveyProjectSnapshot>> RenameSurveyProjectAsync(
        SurveyProjectRenameRequest request,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.RenameProjectAsync(request, cancellationToken);

    public Task<SurveyOperationResult<bool>> DeleteSurveyProjectAsync(
        SurveyProjectDeleteRequest request,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.DeleteProjectAsync(request, cancellationToken);

    public Task<Stream> OpenSurveyAssetAsync(
        Guid projectId,
        SurveyAssetReference asset,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.OpenAssetAsync(projectId, asset, cancellationToken);

    public Task<SurveyOperationResult<SurveyProjectSnapshot>> EditSurveyLayerAsync(
        SurveyLayerEditRequest request,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.EditLayerAsync(request, cancellationToken);

    public Task<SurveyOperationResult<SurveyProjectSnapshot>> SetSurveyProjectStateAsync(
        SurveyProjectStateRequest request,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.SetProjectStateAsync(request, cancellationToken);

    public Task<SurveyOperationResult<SurveyDualOutput>> RenderSurveyOutputsAsync(
        Guid projectId,
        string floorKey,
        CancellationToken cancellationToken = default) =>
        _surveyCoordinator.RenderOutputsAsync(projectId, floorKey, cancellationToken);

    private void SurveyCoordinator_StatusChanged(
        object? sender,
        SurveyStatusSnapshot status)
    {
        void Apply()
        {
            if (!string.IsNullOrWhiteSpace(status.LastMessage))
                _statusMessage = status.LastMessage;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        if (_dispatcher.HasThreadAccess)
            Apply();
        else
            _dispatcher.TryEnqueue(Apply);
    }

    private async Task ActivateSurveyFromQuickScanAsync(
        CapturedGameFrame frame,
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        if (!IsCurrentMatchOperation(operationMatch)
            || operationMatch.Mode != MapRunMode.Normal)
            return;

        var floorKey = NormalizeSurveyFloorKey(_currentFloorKey);
        var predictedEpoch = operationMatch.Version + 1L;
        var start = await _surveyCoordinator.StartAsync(
            new SurveyStartRequest(
                Guid.NewGuid(),
                operationMatch.MatchId,
                predictedEpoch,
                operationMatch.MapClass ?? "S1",
                floorKey,
                null,
                CreateSurveyConfigDigest(),
                GetSurveyAlgorithmVersion(),
                _surveyCoordinator.ArmedResumeProjectId),
            cancellationToken);
        if (!start.Succeeded || start.Value is null)
        {
            _statusMessage = start.Message ?? "测绘模式启动失败。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!IsCurrentMatchOperation(operationMatch))
        {
            await _surveyCoordinator.EndAsync(
                new SurveyEndRequest(
                    Guid.NewGuid(),
                    start.Value.Project.ProjectId,
                    start.Value.Project.Revision,
                    operationMatch.MatchId,
                    predictedEpoch),
                CancellationToken.None);
            return;
        }

        _matchSession.SwitchToSurvey(start.Value.Project.ProjectId);
        if (!_gameMapToggleState.IsOpen)
            _gameMapToggleState.MarkOpen();
        _pendingAlignmentIdentity = null;
        _pendingAlignmentSeed = null;
        _lastRecognition = null;
        _lastAlignmentSession = null;
        _overlay.ClearMap();

        var binding = SurveyCaptureBindingName();
        _statusMessage = $"测绘模式已启动。地图打开后按 {binding} 采集画面。";
        await _surveyCoordinator.SetRuntimeStateAsync(
            start.Value.Project.ProjectId,
            SurveyRuntimeState.WaitingForNextOpen,
            _statusMessage,
            cancellationToken);
        ShowTransientOverlayStatus(
            MapOverlayStatusLevel.Success,
            "已进入测绘模式",
            $"按 {binding} 收集当前地图画面",
            "打开/关闭地图不会自动采集",
            frame.ClientBounds,
            frame.WindowHandle);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task HandleSurveyMapOpenAsync(MapGameToggleTransition toggle)
    {
        var operationMatch = _matchSession.Snapshot;
        if (operationMatch.Mode != MapRunMode.Survey)
            return;
        if (operationMatch.SurveyProjectId is null)
        {
            _statusMessage = "当前没有已激活的测绘项目，无法采集画面。";
            ShowSurveyCaptureStatus(MapOverlayStatusLevel.Failure, "测绘项目未激活", _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var binding = SurveyCaptureBindingName();
        var message = _surveyCoordinator.Status.RuntimeState == SurveyRuntimeState.Paused
            ? "测绘已暂停，当前地图不会被采集。"
            : $"地图已打开，按 {binding} 收集当前画面。";
        await _surveyCoordinator.SetRuntimeStateAsync(
            operationMatch.SurveyProjectId.Value,
            _surveyCoordinator.Status.RuntimeState == SurveyRuntimeState.Paused
                ? SurveyRuntimeState.Paused
                : SurveyRuntimeState.WaitingForNextOpen,
            message,
            CurrentMatchCancellationToken);
        _statusMessage = message;
        ShowSurveyCaptureStatus(
            _surveyCoordinator.Status.RuntimeState == SurveyRuntimeState.Paused
                ? MapOverlayStatusLevel.Warning
                : MapOverlayStatusLevel.Success,
            "测绘地图已打开",
            message);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task HandleSurveyMapClosedAsync()
    {
        var operationMatch = _matchSession.Snapshot;
        if (operationMatch.Mode != MapRunMode.Survey
            || operationMatch.SurveyProjectId is not { } projectId)
            return;
        var message = $"地图已关闭；再次打开后按 {SurveyCaptureBindingName()} 采集。";
        if (_surveyCoordinator.Status.RuntimeState != SurveyRuntimeState.Paused)
            await _surveyCoordinator.SetRuntimeStateAsync(
                projectId,
                SurveyRuntimeState.WaitingForNextOpen,
                message,
                CurrentMatchCancellationToken);
        _statusMessage = message;
        ShowSurveyCaptureStatus(MapOverlayStatusLevel.Warning, "测绘等待地图", message);
    }

    private async Task CaptureSurveyFrameOnDemandAsync()
    {
        var operationMatch = _matchSession.Snapshot;
        if (operationMatch.Mode != MapRunMode.Survey
            || operationMatch.SurveyProjectId is null)
            return;
        if (_surveyCoordinator.Status.RuntimeState == SurveyRuntimeState.Paused)
        {
            _statusMessage = "测绘已暂停，无法采集画面。";
            ShowSurveyCaptureStatus(MapOverlayStatusLevel.Warning, "测绘已暂停", _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (!_gameMapToggleState.IsOpen)
        {
            _statusMessage = $"请先打开游戏地图，再按 {SurveyCaptureBindingName()} 采集。";
            ShowSurveyCaptureStatus(MapOverlayStatusLevel.Warning, "无法采集", _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var cancellationToken = CurrentMatchCancellationToken;
        var toggleVersion = 0;
        var restoreOverlay = false;
        if (!await _scanGate.WaitAsync(0, cancellationToken))
        {
            _statusMessage = "已有测绘采集正在进行，请稍候。";
            ShowSurveyCaptureStatus(MapOverlayStatusLevel.Warning, "采集繁忙", _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "Survey capture lock acquired",
                details: new()
                {
                    ["outcome"] = "scan-gate-acquired",
                    ["operationEpoch"] = operationMatch.OperationEpoch
                });
            toggleVersion = _gameMapToggleState.Version;
            restoreOverlay = TryGetSurveyOverlayVisibility();
            ShowSurveyCaptureStatus(
                MapOverlayStatusLevel.Warning,
                "正在采集测绘画面",
                "请保持游戏地图稳定。");
            if (restoreOverlay)
                TrySurveyOverlayOperation("overlay-hide", _overlay.Hide);
            await _surveyCoordinator.SetRuntimeStateAsync(
                operationMatch.SurveyProjectId.Value,
                SurveyRuntimeState.WaitingForStableFrame,
                "正在等待稳定地图画面…",
                cancellationToken);
            using var frame = await CaptureStableViewportAsync(
                "survey-observation",
                cancellationToken,
                _surveyCaptureTuning);
            if (frame is null
                || !IsCurrentMatchOperation(operationMatch)
                || !_gameMapToggleState.IsOpen
                || _gameMapToggleState.Version != toggleVersion)
            {
                var failureMessage = frame is null
                    ? "未能取得稳定地图画面。"
                    : "地图在稳定画面确认期间已关闭，本次采集已取消。";
                await RecordSurveyCaptureFailureAsync(
                    operationMatch,
                    toggleVersion,
                    failureMessage);
                _statusMessage = failureMessage;
                ShowSurveyCaptureStatus(
                    MapOverlayStatusLevel.Failure,
                    "测绘采集失败",
                    failureMessage);
                return;
            }

            var snapshot = await _surveyCoordinator.GetProjectAsync(
                operationMatch.SurveyProjectId.Value,
                cancellationToken);
            if (snapshot is null)
            {
                _statusMessage = "当前测绘项目已经不存在，无法保存采集画面。";
                ShowSurveyCaptureStatus(MapOverlayStatusLevel.Failure, "测绘采集失败", _statusMessage);
                return;
            }
            var result = await AddSurveyFrameAsync(
                frame,
                operationMatch,
                toggleVersion,
                snapshot.Project.Revision,
                cancellationToken);
            _logCollector.Append(
                MapLogCategory.Session,
                result.Succeeded ? MapLogLevel.Info : MapLogLevel.Warning,
                result.Succeeded
                    ? "Survey observation committed"
                    : "Survey observation commit rejected",
                details: new()
                {
                    ["outcome"] = result.Succeeded
                        ? "observation-commit-succeeded"
                        : "observation-commit-failed",
                    ["projectId"] = operationMatch.SurveyProjectId,
                    ["operationEpoch"] = operationMatch.OperationEpoch,
                    ["mapToggleVersion"] = toggleVersion,
                    ["message"] = result.Message
                });
            _statusMessage = result.Message
                ?? (result.Succeeded ? "测绘图层已记录。" : "测绘图层记录失败。");
            ShowSurveyCaptureStatus(
                result.Succeeded ? MapOverlayStatusLevel.Success : MapOverlayStatusLevel.Failure,
                result.Succeeded ? "测绘画面已收集" : "测绘采集失败",
                _statusMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _statusMessage = "测绘采集已取消。";
            ShowSurveyCaptureStatus(MapOverlayStatusLevel.Warning, "测绘采集已取消", _statusMessage);
        }
        catch (Exception exception)
        {
            _statusMessage = $"测绘采集失败：{exception.Message}";
            try
            {
                await RecordSurveyCaptureFailureAsync(
                    operationMatch,
                    toggleVersion,
                    _statusMessage);
            }
            catch (Exception recordException)
            {
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Error,
                    $"测绘采集失败记录未能保存 · {recordException.Message}",
                    details: new() { ["exception"] = recordException.ToString() });
                await _surveyCoordinator.SetRuntimeStateAsync(
                    operationMatch.SurveyProjectId.Value,
                    SurveyRuntimeState.WaitingForNextOpen,
                    _statusMessage,
                    CancellationToken.None);
            }
            ShowSurveyCaptureStatus(MapOverlayStatusLevel.Failure, "测绘采集失败", _statusMessage);
        }
        finally
        {
            SurveyCaptureCleanup.Complete(
                _scanGate,
                () =>
                {
                    if (restoreOverlay
                        && IsCurrentMatchOperation(operationMatch)
                        && !TryGetSurveyOverlayVisibility())
                    {
                        _overlay.Show();
                    }
                },
                () => StateChanged?.Invoke(this, EventArgs.Empty),
                LogSurveyCleanupFailure);
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                "Survey capture lock released",
                details: new()
                {
                    ["outcome"] = "scan-gate-released",
                    ["operationEpoch"] = operationMatch.OperationEpoch
                });
        }
    }

    private bool TryGetSurveyOverlayVisibility()
    {
        try
        {
            return _overlay.IsVisible;
        }
        catch (Exception exception)
        {
            LogSurveyCleanupFailure("overlay-visibility", exception);
            return false;
        }
    }

    private void TrySurveyOverlayOperation(string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            LogSurveyCleanupFailure(operation, exception);
        }
    }

    private void LogSurveyCleanupFailure(
        string operation,
        Exception exception)
    {
        try
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                $"Survey UI cleanup failed: {operation}",
                details: new()
                {
                    ["outcome"] = "overlay-operation-failed",
                    ["operation"] = operation,
                    ["exceptionType"] = exception.GetType().FullName,
                    ["exception"] = exception.ToString()
                });
        }
        catch
        {
        }
    }

    private string SurveyCaptureBindingName() =>
        _settings?.SaveMapCacheBinding is { IsConfigured: true } binding
            ? binding.DisplayName
            : "未配置的“保存地图缓存”按键";

    private void ShowSurveyCaptureStatus(
        MapOverlayStatusLevel level,
        string title,
        string message)
    {
        if (!_lastGameBounds.IsValid || _lastGameWindowHandle == IntPtr.Zero)
            return;
        ShowTransientOverlayStatus(
            level,
            title,
            message,
            string.Empty,
            _lastGameBounds,
            _lastGameWindowHandle);
    }

    private async Task EndSurveyMatchAsync(MapMatchSnapshot endingMatch)
    {
        if (endingMatch.Mode != MapRunMode.Survey
            || endingMatch.SurveyProjectId is not { } projectId)
            return;
        try
        {
            var snapshot = await _surveyCoordinator.GetProjectAsync(
                projectId,
                CancellationToken.None);
            if (snapshot is null)
                return;
            var result = await _surveyCoordinator.EndAsync(
                new SurveyEndRequest(
                    Guid.NewGuid(),
                    projectId,
                    snapshot.Project.Revision,
                    endingMatch.MatchId,
                    endingMatch.OperationEpoch),
                CancellationToken.None);
            if (!result.Succeeded)
            {
                _logCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Warning,
                    $"测绘项目结束失败 · project={projectId:N} · {result.Message}");
            }
        }
        catch (Exception exception)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Error,
                $"测绘项目结束异常 · project={projectId:N}",
                details: new() { ["exception"] = exception.ToString() });
        }
    }

    private async Task<SurveyOperationResult<SurveyObservationCommitResult>> AddSurveyFrameAsync(
        CapturedGameFrame frame,
        MapMatchSnapshot match,
        int mapToggleVersion,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (match.SurveyProjectId is not { } projectId)
        {
            return SurveyOperationResult<SurveyObservationCommitResult>.Failure(
                SurveyErrorCode.InvalidState,
                "当前对局没有测绘项目。");
        }

        Cv2.ImEncode(".png", frame.Image, out var bytes);
        var floorKey = NormalizeSurveyFloorKey(_currentFloorKey ?? match.FloorKey);
        var capture = new SurveyCaptureContext(
            match.MatchId,
            match.OperationEpoch,
            mapToggleVersion,
            DateTimeOffset.UtcNow,
            (int)Math.Round(frame.ClientBounds.Width),
            (int)Math.Round(frame.ClientBounds.Height),
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
            new SurveyPixelRect(
                frame.ViewportBounds.X,
                frame.ViewportBounds.Y,
                frame.ViewportBounds.Width,
                frame.ViewportBounds.Height),
            floorKey,
            CreateSurveyConfigDigest(),
            GetSurveyAlgorithmVersion());
        return await _surveyCoordinator.AddObservationAsync(
            new SurveyObservationRequest(
                Guid.NewGuid(),
                projectId,
                expectedRevision,
                new SurveyEncodedFrame(
                    bytes,
                    ".png",
                    "image/png",
                    frame.Image.Width,
                    frame.Image.Height,
                    capture)),
            cancellationToken);
    }

    private string CreateSurveyConfigDigest()
    {
        var preprocessing = _config.Get<SurveyPreprocessingTuning>("survey.preprocessing");
        var registration = _config.Get<SurveyRegistrationTuning>("survey.registration");
        var storage = _config.Get<SurveyStorageTuning>("survey.storage");
        var visual = _config.Get<SurveyFusionTuning>("survey.fusion.visual");
        var structure = _config.Get<SurveyFusionTuning>("survey.fusion.structure");
        var payload = string.Join('|',
            "survey-schema-1",
            _config.ActiveResolutionPreset,
            Invariant(_surveyCaptureTuning.StableFrameDelayMilliseconds),
            Invariant(_surveyCaptureTuning.MaximumCaptureMilliseconds),
            Invariant(_surveyCaptureTuning.QueueCapacity),
            Invariant(preprocessing.MaximumFeatureCount),
            Invariant(preprocessing.EdgeLowThreshold),
            Invariant(preprocessing.EdgeHighThreshold),
            Invariant(preprocessing.MapCanvasLeft),
            Invariant(preprocessing.MapCanvasTop),
            Invariant(preprocessing.MapCanvasRight),
            Invariant(preprocessing.MapCanvasBottom),
            Invariant(preprocessing.ShapeOpeningDivisor),
            Invariant(preprocessing.ShapeClosingDivisor),
            Invariant(preprocessing.MinimumShapeComponentAreaRatio),
            Invariant(preprocessing.MinimumShapeThicknessFactor),
            Invariant(preprocessing.MaximumShapeHoleAreaRatio),
            Invariant(registration.CandidateCount),
            Invariant(registration.RatioTest),
            Invariant(registration.MinimumMatches),
            Invariant(registration.MinimumInliers),
            Invariant(registration.MinimumInlierRatio),
            Invariant(registration.MaximumResidualPixels),
            Invariant(registration.MinimumScale),
            Invariant(registration.MaximumScale),
            Invariant(storage.AssetRetentionDays),
            Invariant(storage.MaximumProjectLayers),
            Invariant(visual.MaximumOutputPixels),
            Invariant(structure.StructureBinaryThreshold));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        static string Invariant(object value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task RecordSurveyCaptureFailureAsync(
        MapMatchSnapshot match,
        long toggleVersion,
        string message)
    {
        if (match.SurveyProjectId is not { } projectId)
            return;
        var snapshot = await _surveyCoordinator.GetProjectAsync(projectId, CancellationToken.None);
        if (snapshot is null)
            return;
        await _surveyCoordinator.RecordCaptureFailureAsync(
            new SurveyCaptureFailureRequest(
                Guid.NewGuid(),
                projectId,
                snapshot.Project.Revision,
                match.MatchId,
                match.OperationEpoch,
                toggleVersion,
                NormalizeSurveyFloorKey(_currentFloorKey ?? match.FloorKey),
                DateTimeOffset.UtcNow,
                SurveyErrorCode.CaptureFailed,
                message),
            CancellationToken.None);
    }

    private static string GetSurveyAlgorithmVersion() =>
        typeof(SessionOrchestrator).Assembly.GetName().Version?.ToString()
        ?? "1.0.0";

    private static string NormalizeSurveyFloorKey(string? floorKey) =>
        string.IsNullOrWhiteSpace(floorKey) ? "1f" : floorKey.Trim().ToLowerInvariant();
}
