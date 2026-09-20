namespace IDVBuff.UpdateCore;

public enum MapSubscriptionReconciliationAction
{
    None,
    RemoveSubscription,
    ForceReapply
}

public static class MapSubscriptionReconciliation
{
    public static MapSubscriptionReconciliationAction Evaluate(
        MapSubscriptionRecord record,
        IReadOnlySet<Guid> installedMapIds)
    {
        if (record.InstalledMapIds.Count == 0)
            return MapSubscriptionReconciliationAction.None;
        var presentCount = record.InstalledMapIds.Count(installedMapIds.Contains);
        if (presentCount == 0)
            return MapSubscriptionReconciliationAction.RemoveSubscription;
        return presentCount == record.InstalledMapIds.Count
            ? MapSubscriptionReconciliationAction.None
            : MapSubscriptionReconciliationAction.ForceReapply;
    }
}
