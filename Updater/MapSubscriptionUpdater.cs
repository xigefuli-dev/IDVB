using IDVBuff.UpdateCore;

namespace IDVBuff.Updater;

internal static class MapSubscriptionUpdater
{
    public static async Task<int> RunAsync(UpdaterLaunchOptions options)
    {
        try
        {
            var root = options.SubscriptionRoot
                ?? throw new ArgumentException("地图订阅目录为空。");
            var officialPublicKey = MapSubscriptionTrust.LoadOfficialPublicKey(AppContext.BaseDirectory);
            var summary = await new MapSubscriptionUpdateEngine().UpdateAllAsync(
                root,
                officialPublicKey,
                CancellationToken.None);
            UpdateLog.Write(
                $"Map subscriptions checked={summary.Checked}, prepared={summary.Prepared}, failed={summary.Failed}");
            return summary.Failed == 0 ? 0 : 3;
        }
        catch (Exception exception)
        {
            UpdateLog.Write("Map subscription update failed", exception);
            return 4;
        }
    }
}
