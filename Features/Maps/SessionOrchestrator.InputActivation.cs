using IDVBuff.Features.QuickStart;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task SetEnabledAsync(bool v)
    {
        await _inputSettingsGate.WaitAsync();
        try { await SetEnabledCoreAsync(v); }
        finally { _inputSettingsGate.Release(); }
    }

    private async Task SetEnabledCoreAsync(bool v)
    {
        _logCollector.Append(MapLogCategory.System, MapLogLevel.Info,
            "运行层启用检查", details: new()
            {
                ["requestedEnabled"] = v, ["previousEnabled"] = _settings?.IsEnabled,
                ["safeMode"] = App.IsSafeMode, ["mapCount"] = _recognition.TotalMapCount,
                ["mapToggleConfigured"] = _settings?.GameMapToggleBinding.IsConfigured,
                ["controlPanelConfigured"] = _settings?.ControlPanelToggleBinding.IsConfigured,
                ["quickScanConfigured"] = _settings?.QuickScanBinding.IsConfigured
            });
        try
        {
            if (_settings is null)
                throw new InvalidOperationException("SessionOrchestrator has not been initialized.");
            await InputRuntimeActivation.ApplyAsync(_settings, v, () =>
            {
                if (_settings.IsEnabled && !TryValidateEnablePrerequisites(out var failureMessage))
                    throw new InvalidOperationException(failureMessage);
                ApplyBindings();
                if (_settings.IsEnabled && _settings.ContinuousObservationEnabled)
                    StartMapObservation();
            }, SaveSettingsAsync);
            _statusMessage = v ? "运行层已开启。" : "运行层已关闭。";
            _logCollector.Append(MapLogCategory.System, MapLogLevel.Info,
                "运行层状态已应用并保存", details: new() { ["enabled"] = _settings.IsEnabled });
        }
        catch (Exception failure)
        {
            if (_settings is not null)
            {
                _statusMessage = $"运行层未开启：{failure.Message}";
                LogInputHandlerOutcome("runtime-enable", "handler-failed", failure);
            }
            throw;
        }
        finally
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Applies the first-run recommended profile to the active runtime and
    /// persists it. Values not specified by the recommendation remain at the
    /// normal runtime defaults.
    /// </summary>
    public async Task ApplyQuickStartRecommendedSettingsAsync()
    {
        await _inputSettingsGate.WaitAsync();
        try { await ApplyQuickStartRecommendedSettingsCoreAsync(); }
        finally { _inputSettingsGate.Release(); }
    }

    private async Task ApplyQuickStartRecommendedSettingsCoreAsync()
    {
        if (_settings is null)
            throw new InvalidOperationException("SessionOrchestrator has not been initialized.");

        var recommended = QuickStartRecommendedSettings.CreateRecommendation1();
        recommended.Normalize();
        await _researchCollector.SetEnabledAsync(recommended.CollectAlignmentResearchData);
        _settings = recommended;
        _logCollector.IsEnabled = recommended.CollectLogs;
        await SetEnabledCoreAsync(recommended.IsEnabled);
        ApplyDisplaySettingsToOverlay();
        await SaveSettingsAsync();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly SemaphoreSlim _inputSettingsGate = new(1, 1);

}
