namespace IDVBuff.Features.Maps;

internal static partial class MapCvAlignmentService
{
    private static Dictionary<string, object?> CreateLiveStructureLogDetails(
        CapturedGameFrame frame,
        MapStructureFeatures features,
        PreprocessTiming timing,
        string source,
        double originalExtractionMilliseconds,
        double currentExtractionMilliseconds,
        double referenceImageLoadMilliseconds,
        double referenceCacheMilliseconds,
        int liveIgnoreRegionCount,
        int dynamicIgnoreRegionCount,
        string? route = null,
        MapStructurePreprocessingProfile? requestedProfile = null) =>
        new()
        {
            ["source"] = source,
            ["route"] = route,
            ["originalExtractionMs"] = originalExtractionMilliseconds,
            ["currentExtractionMs"] = currentExtractionMilliseconds,
            ["referenceImageLoadMs"] = referenceImageLoadMilliseconds,
            ["referenceCacheMs"] = referenceCacheMilliseconds,
            ["imageWidth"] = frame.Image.Width,
            ["imageHeight"] = frame.Image.Height,
            ["requestedPreprocessingProfile"] =
                (requestedProfile ?? timing.Profile).ToString(),
            ["preprocessingProfile"] = timing.Profile.ToString(),
            ["descriptorExtractionSkipped"] =
                timing.DescriptorExtractionSkipped,
            ["keyPointCount"] = features.KeyPoints.Length,
            ["descriptorRows"] = features.Descriptors.Rows,
            ["claheBlurMs"] = timing.ClaheBlurMs,
            ["nuisanceMaskMs"] = timing.NuisanceMaskMs,
            ["structureMaskMs"] = timing.StructureMs,
            ["edgesMs"] = timing.EdgesMs,
            ["featuresMs"] = timing.FeaturesMs,
            ["pyramidMs"] = timing.PyramidMs,
            ["repeatedRegionsMs"] = timing.RepeatedMs,
            ["visibleMaskMs"] = timing.VisibleMaskMs,
            ["stageTotalMs"] = timing.TotalMs,
            ["structureComponentCount"] = timing.StructureComponentCount,
            ["keptStructureComponentCount"] =
                timing.KeptStructureComponentCount,
            ["dominantComponentArea"] = timing.DominantComponentArea,
            ["dominantComponentX"] = timing.DominantComponentX,
            ["dominantComponentY"] = timing.DominantComponentY,
            ["dominantComponentWidth"] = timing.DominantComponentWidth,
            ["dominantComponentHeight"] = timing.DominantComponentHeight,
            ["keptStructureBoundsX"] = timing.KeptStructureBoundsX,
            ["keptStructureBoundsY"] = timing.KeptStructureBoundsY,
            ["keptStructureBoundsWidth"] = timing.KeptStructureBoundsWidth,
            ["keptStructureBoundsHeight"] = timing.KeptStructureBoundsHeight,
            ["liveIgnoreRegionCount"] = liveIgnoreRegionCount,
            ["dynamicIgnoreRegionCount"] = dynamicIgnoreRegionCount
        };

    private static MapStructurePreprocessingProfile
        ResolveLiveStructurePreprocessingProfile(
            MapScaleSearchPolicy scaleSearchPolicy,
            bool isTracking,
            MapStructureRegistrationTuning tuning)
    {
        if (!tuning.EnableFeatureVoting
            || scaleSearchPolicy == MapScaleSearchPolicy.Fixed
            || isTracking)
        {
            return MapStructurePreprocessingProfile.EdgesOnly;
        }

        return MapStructurePreprocessingProfile.EdgesAndFeatures;
    }
}
