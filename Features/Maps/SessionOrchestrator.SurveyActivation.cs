using IDVBuff.Survey.Domain;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private async Task BeginSurveyMatchAsync(string mapClass)
    {
        await BeginMatchAsync(mapClass);
        try
        {
            await ActivateSurveyMatchAsync();
        }
        catch
        {
            await EndMatchAsync();
            throw;
        }
    }

    private async Task<MapMatchSnapshot> ActivateSurveyMatchAsync()
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            var operationMatch = _matchSession.Snapshot;
            if (!IsCurrentMatchOperation(operationMatch)
                || operationMatch.Mode != MapRunMode.Normal)
            {
                throw new InvalidOperationException("只能从正在进行的普通对局激活测绘模式。");
            }

            using var frame = await CaptureSurveyViewportOnceAsync(
                "direct-survey-activation",
                CurrentMatchCancellationToken);
            if (frame is null)
                throw new InvalidOperationException(
                    _lastStableCaptureFailureReason ?? "无法取得当前地图画面，测绘模式未激活。");

            // Keep the control-panel entry on the exact same activation path as
            // the candidate window's "start survey" action.
            await ActivateSurveyFromQuickScanAsync(
                frame,
                operationMatch,
                CurrentMatchCancellationToken);

            var surveyMatch = _matchSession.Snapshot;
            if (!IsCurrentMatchOperation(surveyMatch)
                || surveyMatch.Mode != MapRunMode.Survey)
            {
                throw new InvalidOperationException(
                    _statusMessage ?? "测绘模式启动失败。");
            }
            return surveyMatch;
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }
}
