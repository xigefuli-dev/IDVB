namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public async Task SetUsePrebuiltStructureLineAsync(bool enabled)
    {
        if (_settings is null)
            throw new InvalidOperationException("SessionOrchestrator has not been initialized.");
        var previous = _settings.StructureRegistrationTuning.UsePrebuiltStructureLine;
        _settings.StructureRegistrationTuning.UsePrebuiltStructureLine = enabled;
        try
        {
            await SaveSettingsAsync();
        }
        catch
        {
            _settings.StructureRegistrationTuning.UsePrebuiltStructureLine = previous;
            throw;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
