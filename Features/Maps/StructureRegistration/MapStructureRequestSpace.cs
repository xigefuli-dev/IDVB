using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class MapStructureRequestSpace
{
    internal static MapStructureRegistrationRequest ToComputationSpace(
        MapStructureRegistrationRequest source,
        double ratio)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(ratio) || ratio <= 1.000001d)
            throw new ArgumentOutOfRangeException(nameof(ratio));

        var scale = 1d / ratio;
        return new MapStructureRegistrationRequest
        {
            ReferenceImage = source.ReferenceImage,
            Channel = source.Channel,
            LiveRoi = source.LiveRoi,
            PhysicalPixelsPerLivePixel = 1d,
            ViewportBounds = Scale(source.ViewportBounds, scale),
            LockedTransform = Scale(source.LockedTransform, scale),
            Tuning = source.Tuning,
            ScaleSearchPolicy = source.ScaleSearchPolicy,
            RestrictSearchToLockedTransform = source.RestrictSearchToLockedTransform,
            TrackingMode = source.TrackingMode,
            ForceBestCandidate = source.ForceBestCandidate,
            FixedRotationDegrees = source.FixedRotationDegrees,
            ValidMapBounds = source.ValidMapBounds,
            PredictedViewportOrigin = source.PredictedViewportOrigin,
            PlayerPrior = source.PlayerPrior,
            CandidateHistory = source.CandidateHistory
                .Select(x => Scale(x, scale))
                .ToArray(),
            LiveIgnoreRegions = source.LiveIgnoreRegions,
            DynamicIgnoreRegions = source.DynamicIgnoreRegions
                .Select(x => Scale(x, scale))
                .ToArray(),
            DebugOutputDirectory = source.DebugOutputDirectory,
            PreparedReference = source.PreparedReference,
            PreparedLive = source.PreparedLive,
            PreparedOriginalLive = source.PreparedOriginalLive,
            LowStructurePlan = ToComputationPlan(source.LowStructurePlan, ratio),
            SideEntrancePrior = source.SideEntrancePrior
        };
    }

    internal static MapOverlayTransform ToPhysicalTransform(
        MapOverlayTransform value,
        double ratio) => Scale(value, ratio);

    private static MapScreenRect Scale(MapScreenRect value, double scale) =>
        new(value.X * scale, value.Y * scale, value.Width * scale, value.Height * scale);

    private static Rect Scale(Rect value, double scale) => new(
        (int)Math.Round(value.X * scale),
        (int)Math.Round(value.Y * scale),
        Math.Max(1, (int)Math.Round(value.Width * scale)),
        Math.Max(1, (int)Math.Round(value.Height * scale)));

    private static MapOverlayTransform Scale(
        MapOverlayTransform value,
        double scale) =>
        MapCanonicalTransformMath.ToPhysicalTransform(value, scale);

    private static LowStructureAlignmentPlan? ToComputationPlan(
        LowStructureAlignmentPlan? plan,
        double ratio) =>
        plan is null
            ? null
            : plan with
            {
                Scales = plan.Scales.Select(scale => scale / ratio).ToArray()
            };

    private static MapSimilarityTransform Scale(
        MapSimilarityTransform value,
        double scale) => new()
    {
        Scale = value.Scale * scale,
        RotationDegrees = value.RotationDegrees,
        TranslationX = value.TranslationX * scale,
        TranslationY = value.TranslationY * scale
    };
}
