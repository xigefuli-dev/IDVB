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
            ["generationFingerprint"] = timing.GenerationFingerprint,
            ["edgeComposition"] = timing.EdgeComposition.ToString(),
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
            ["edgePixelCount"] = timing.EdgePixelCount,
            ["edgeComponentCount"] = timing.EdgeComponentCount,
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
/*
 * 文件职责：MapCvAlignmentService.Logging。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
