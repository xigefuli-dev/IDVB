using Microsoft.UI.Xaml;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Features.Maps;

public sealed partial class MapControlPanelWindow
{
    private void ApplySurveyState(MapMatchSnapshot snapshot)
    {
        if (snapshot.Mode != MapRunMode.Survey)
            return;
        var status = _getSurveyStatus();
        if (status.ProjectId is null)
            return;
        _stateText.Text = $"测绘中 · {status.ProjectName} · {status.FloorKey?.ToUpperInvariant()} · "
            + $"{status.ObservationCount} 个图层（{status.UnregisteredCount} 个未对齐）";
        _messageText.Text = status.RuntimeState == SurveyRuntimeState.Paused
            ? "测绘已暂停，可在地图状态页继续。"
            : status.LastMessage ?? "打开地图后，按“保存地图缓存”绑定收集一个持久图层。";
    }

    private async void SurveyModeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingSurveyToggle)
            return;
        if (!_snapshot.IsStarted)
        {
            Refresh(_snapshot);
            return;
        }
        if (_snapshot.Mode == MapRunMode.Survey || !_surveyModeToggle.IsOn)
            return;
        if (_activateSurveyMatch is null)
        {
            SetSurveyToggle(false);
            _messageText.Text = "当前对局无法直接激活测绘模式。";
            return;
        }

        SetActionsEnabled(false);
        try
        {
            // Activation captures the game immediately. Release the panel's
            // foreground ownership before invoking the capture path.
            Hide();
            var snapshot = await _activateSurveyMatch();
            Refresh(snapshot);
        }
        catch (Exception exception)
        {
            SetSurveyToggle(false);
            _messageText.Text = exception.Message;
        }
        finally
        {
            SetActionsEnabled(true);
        }
    }

    private bool CanChangeSurveyMode(MapMatchSnapshot snapshot) =>
        !snapshot.IsStarted
        || (snapshot.Mode == MapRunMode.Normal && _activateSurveyMatch is not null);

    private void SetSurveyToggle(bool isOn)
    {
        if (_surveyModeToggle.IsOn == isOn)
            return;
        _updatingSurveyToggle = true;
        try
        {
            _surveyModeToggle.IsOn = isOn;
        }
        finally
        {
            _updatingSurveyToggle = false;
        }
    }
}
