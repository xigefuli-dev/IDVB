namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task SetMapImprovementDataCollectionEnabledAsync(bool enabled)
    {
        if (_settings is null)
            throw new InvalidOperationException(
                "SessionOrchestrator has not been initialized.");

        var previousLearning = _settings.ContinuousMapLearningEnabled;
        var previousResearch = _settings.CollectAlignmentResearchData;
        try
        {
            await _researchCollector.SetEnabledAsync(enabled);
            _settings.ContinuousMapLearningEnabled = enabled;
            _settings.CollectAlignmentResearchData = enabled;
            await SaveSettingsAsync();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            _settings.ContinuousMapLearningEnabled = previousLearning;
            _settings.CollectAlignmentResearchData = previousResearch;
            try
            {
                await _researchCollector.SetEnabledAsync(previousResearch);
            }
            catch (Exception rollbackException)
            {
                _logCollector.Append(
                    MapLogCategory.System,
                    MapLogLevel.Error,
                    "模型改进数据采集状态回滚失败。",
                    details: new()
                    {
                        ["exceptionType"] = rollbackException.GetType().FullName,
                        ["exception"] = rollbackException.ToString()
                    });
            }
            throw;
        }
    }
}
