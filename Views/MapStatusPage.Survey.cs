namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private readonly SurveyStatusCard _surveyStatusCard = new();

    private async void SurveyStatusCard_PauseRequested(object? sender, bool paused)
    {
        await _runtime.PauseSurveyAsync(paused);
        TryRefresh("survey-pause");
    }
}
