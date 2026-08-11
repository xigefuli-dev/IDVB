using IDVBuff.Survey.Domain;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task PauseSurveyAsync(
        bool paused,
        CancellationToken cancellationToken = default)
    {
        var status = _surveyCoordinator.Status;
        if (status.ProjectId is not { } projectId)
            return;
        var message = paused
            ? "测绘已暂停；保存地图缓存按键暂时不会采集画面。"
            : _gameMapToggleState.IsOpen
                ? $"测绘已继续；按 {SurveyCaptureBindingName()} 采集当前地图画面。"
                : "测绘已继续，等待游戏地图打开。";
        await _surveyCoordinator.SetRuntimeStateAsync(
            projectId,
            paused
                ? SurveyRuntimeState.Paused
                : _gameMapToggleState.IsOpen
                    ? SurveyRuntimeState.WaitingForNextOpen
                    : SurveyRuntimeState.WaitingForMapOpen,
            message,
            cancellationToken);
        _statusMessage = message;
        ShowSurveyCaptureStatus(
            paused ? MapOverlayStatusLevel.Warning : MapOverlayStatusLevel.Success,
            paused ? "测绘已暂停" : "测绘已继续",
            message);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
