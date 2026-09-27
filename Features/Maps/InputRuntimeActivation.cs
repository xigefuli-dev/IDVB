namespace IDVBuff.Features.Maps;

/// <summary>Installs input before persisting activation; failures leave the
/// runtime disabled and report any secondary cleanup or persistence errors.</summary>
internal static class InputRuntimeActivation
{
    public static async Task ApplyAsync(
        MapRuntimeSettings settings,
        bool enabled,
        Action applyBindings,
        Func<Task> saveSettings)
    {
        try
        {
            settings.IsEnabled = enabled;
            applyBindings();
            await saveSettings();
        }
        catch (Exception failure)
        {
            settings.IsEnabled = false;
            var failures = new List<Exception> { failure };
            try { applyBindings(); }
            catch (Exception cleanupFailure) { failures.Add(cleanupFailure); }
            try { await saveSettings(); }
            catch (Exception saveFailure) { failures.Add(saveFailure); }
            if (failures.Count > 1)
                throw new AggregateException("运行层启用失败，关闭状态未能完整保存或清理。", failures);
            throw;
        }
    }
}
