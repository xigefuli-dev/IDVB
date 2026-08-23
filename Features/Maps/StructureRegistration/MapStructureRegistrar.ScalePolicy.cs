namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    internal static bool ShouldUseReciprocalScale(
        MapAlignmentChannel channel,
        double baselineScale,
        bool restrictSearchToLockedTransform) =>
        channel != MapAlignmentChannel.LowStructure
        && double.IsFinite(baselineScale)
        && baselineScale < 1.0d
        && !restrictSearchToLockedTransform;

    private static double ResolveScaleSearchRadius(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning)
    {
        if (request.Channel == MapAlignmentChannel.LowStructure)
        {
            return request.TrackingMode
                ? tuning.TrackingScaleSearchRadius
                : tuning.ScaleSearchRadius;
        }

        return request.TrackingMode
            ? Math.Max(
                tuning.TrackingScaleSearchRadius,
                StructureRegistrationRules.TrackingScaleSearchRadius)
            : Math.Max(
                tuning.ScaleSearchRadius,
                StructureRegistrationRules.ScaleSearchRadius);
    }

    private static IReadOnlyList<double> BuildRegistrationScaleHypotheses(
        MapStructureRegistrationRequest request,
        MapStructureRegistrationTuning tuning,
        double effectiveBaseline,
        double baselineScale,
        double scaleSearchRadius)
    {
        if (request.Channel == MapAlignmentChannel.LowStructure
            && request.ScaleSearchPolicy == MapScaleSearchPolicy.Search)
        {
            return MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
                tuning.LowStructureMinimumScale,
                tuning.LowStructureMaximumScale,
                tuning.LowStructureScaleHypothesisCount,
                tuning.MinimumUsableScale,
                baselineScale);
        }

        return MapStructureScaleSearch.BuildScaleHypotheses(
            effectiveBaseline,
            request.ScaleSearchPolicy == MapScaleSearchPolicy.Search,
            scaleSearchRadius,
            tuning.ScaleSearchStep);
    }
}
