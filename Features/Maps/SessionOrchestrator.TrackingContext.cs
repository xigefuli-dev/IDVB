using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    // Shared by the mutually exclusive ORB, VPSG and Python tracking backends.
    private sealed record OrbTrackingContext(
        long Generation,
        MapMatchSnapshot Match,
        MapGameToggleTransition Toggle,
        Guid MapId,
        DateTimeOffset MapUpdatedAt,
        string FloorKey,
        AdaptiveScaleKey AdaptiveKey,
        double BaselineScale);
}
