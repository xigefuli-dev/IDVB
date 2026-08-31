using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;

namespace IDVBuff;

public partial class App
{
    private async Task CheckMapSubscriptionsInBackgroundAsync(SessionOrchestrator session)
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
