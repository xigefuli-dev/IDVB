using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.Lifecycle;

namespace IDVBuff;

public partial class App
{
    private void StartStartupBackgroundTasks(SessionOrchestrator session)
    {
        // Offline network retries must never occupy the WinUI dispatcher.
        _ = Task.Run(AutomaticUpdateLauncher.TryLaunch);
        _ = CheckMapSubscriptionsInBackgroundAsync(session);
    }

    private Task CheckMapSubscriptionsInBackgroundAsync(SessionOrchestrator session) =>
        Task.Run(() => CheckMapSubscriptionsCoreAsync(session));

    private async Task CheckMapSubscriptionsCoreAsync(SessionOrchestrator session)
    {
        try
        {
            var service = new MapSubscriptionService(new MapRepository());
            if (!service.GetSubscriptions().Any(item => item.Enabled)) return;
            var result = await service.CheckAndApplyAsync();
            if (result.AppliedCount > 0)
                await session.RefreshMapCacheAsync();
            OutputLog.Write(
                "INFO",
                "MAP/SUBSCRIPTION",
                $"Subscription check completed: checked={result.CheckedCount}, upToDate={result.UpToDateCount}, applied={result.AppliedCount}, failed={result.FailedCount}.");
        }
        catch (Exception exception)
        {
            OutputLog.Write(
                "WARN",
                "MAP/SUBSCRIPTION",
                "Background map subscription update did not complete.",
                exception);
        }
    }
}
