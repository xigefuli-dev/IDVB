namespace IDVBuff.Features.Maps;

internal static class MapOpenAlignmentRouteRules
{
    internal const int MinimumNoDoorStageBudgetMilliseconds = 250;
    internal const int MinimumFeatureRecoveryBudgetMilliseconds = 1000;
    internal const int TargetP50Milliseconds = 1000;
    internal const int TargetP95Milliseconds = 1500;
    internal const int MaximumNoDoorAlignmentBudgetMilliseconds = 1800;
    internal const int VpsgStageBudgetMilliseconds = 600;
    internal const int MinimumVpsgStageBudgetMilliseconds = 450;
    internal const double TargetReliableAlignmentRate = 0.95d;
    internal const double TargetTranslationJitterP95Pixels = 3d;

    internal static bool ShouldPreferLockedSideFeature(
        bool isOtherFloor,
        bool recoveringSelectedIdentity,
        double sideEntrancePriorConfidence) =>
        !isOtherFloor
        && !recoveringSelectedIdentity
        && sideEntrancePriorConfidence > 0d;

    internal static bool ShouldPrioritizeStructureValidation(
        SelectedAlignmentRoute route,
        bool hasAlignmentDeadline) =>
        route == SelectedAlignmentRoute.SideEntrance
        && hasAlignmentDeadline;

    internal static int ResolveNoDoorAlignmentBudgetMilliseconds(
        int configuredMilliseconds) =>
        Math.Clamp(
            configuredMilliseconds,
            MinimumNoDoorStageBudgetMilliseconds,
            MaximumNoDoorAlignmentBudgetMilliseconds);

    internal static double ResolveSingleGlobalRecoveryRadius(
        bool hasFloorCalibration) =>
        MapFloorScaleSearchPolicy.GetRadii(hasFloorCalibration).ExpandedRadius;

    internal static bool IsCompatibleReliableFloorSession(
        MapAlignmentSession? session,
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        string floorKey,
        double minimumConfidence)
    {
        if (session is null
            || session.MapId != mapId
            || session.MapUpdatedAt != mapUpdatedAt
            || !string.Equals(
                session.FloorKey,
                floorKey,
                StringComparison.Ordinal)
            || !double.IsFinite(session.LastConfidence)
            || session.LastConfidence < Math.Clamp(minimumConfidence, 0d, 1d))
        {
            return false;
        }

        return MapSimilarityTransform.FromOverlay(session.LockedTransform)
            .IsValid;
    }
}

internal static class MapNoDoorAlignmentBudgetContext
{
    private static readonly AsyncLocal<Func<int>?> RemainingAccessor = new();

    internal static int? RemainingMilliseconds => RemainingAccessor.Value?.Invoke();

    internal static IDisposable Enter(Func<int> remainingAccessor)
    {
        ArgumentNullException.ThrowIfNull(remainingAccessor);
        var previous = RemainingAccessor.Value;
        RemainingAccessor.Value = remainingAccessor;
        return new Lease(previous);
    }

    private sealed class Lease(Func<int>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            RemainingAccessor.Value = previous;
        }
    }
}
