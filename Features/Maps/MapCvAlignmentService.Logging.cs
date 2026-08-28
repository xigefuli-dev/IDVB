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

    internal static MapStructurePreprocessingProfile
        ResolveLiveStructurePreprocessingProfile(
            MapScaleSearchPolicy scaleSearchPolicy,
            bool isTracking,
            MapStructureRegistrationTuning tuning)
    {
        var requiresContentScaleBootstrap =
            tuning.Channel == MapAlignmentChannel.LowStructure
            && scaleSearchPolicy == MapScaleSearchPolicy.Search
            && !isTracking
            && tuning.LowStructureEnableFeatureScaleEstimate;
        if ((!tuning.EnableFeatureVoting && !requiresContentScaleBootstrap)
            || scaleSearchPolicy == MapScaleSearchPolicy.Fixed
            || isTracking)
        {
            return MapStructurePreprocessingProfile.EdgesOnly;
        }

        return MapStructurePreprocessingProfile.EdgesAndFeatures;
    }

    internal static bool HasLowStructureScaleBasinSupport(
        MapStructureRegistrationResult result,
        MapStructureRegistrationTuning tuning)
    {
        if (!result.Accepted || result.Transform is null)
            return false;
        var selectedScale = result.Transform.ScaleX;
        return result.Candidates.Any(candidate =>
            Math.Abs(candidate.Scale - selectedScale)
                > tuning.ScaleDuplicateTolerance
            && Math.Abs(candidate.Scale - selectedScale)
                / Math.Max(candidate.Scale, selectedScale)
                <= tuning.MaximumScaleChangeRatio
            && MapStructureValidator.ValidateAbsolute(candidate, tuning)
                == MapStructureRejectionReason.None);
    }

    internal static bool HasLowStructureScaleIntegrity(
        MapStructureRegistrationResult result,
        MapScreenRect viewport,
        MapStructureRegistrationTuning tuning)
    {
        if (!result.Accepted
            || result.Transform is null
            || result.ReferenceWidth <= 0
            || result.ReferenceHeight <= 0
            || viewport.Width <= 0d
            || viewport.Height <= 0d)
        {
            return false;
        }

        // The overlay represents the complete reference floor. A transform
        // that makes either reference dimension more than twice the native
        // map viewport is the oversized-overlay failure mode this gate must
        // block, independently of how well one local room can be nested.
        const double maximumProjectedDimensionRatio = 2d;
        var projectedWidth = result.ReferenceWidth
            * Math.Abs(result.Transform.ScaleX);
        var projectedHeight = result.ReferenceHeight
            * Math.Abs(result.Transform.ScaleY);
        if (projectedWidth > viewport.Width * maximumProjectedDimensionRatio
            || projectedHeight > viewport.Height * maximumProjectedDimensionRatio)
        {
            return false;
        }

        if (HasLowStructureScaleBasinSupport(result, tuning))
            return true;

        // Coarse hypotheses are roughly 10-12% apart. A sharply defined
        // variant can therefore have no second absolute pass even though the
        // selected transform has strong bidirectional projection evidence.
        // Do not require a deliberately wrong neighbouring scale to pass.
        var selectedScale = result.Transform.ScaleX;
        var selected = result.Candidates
            .Where(candidate =>
                Math.Abs(candidate.Scale - selectedScale)
                    <= tuning.ScaleDuplicateTolerance)
            .OrderBy(candidate => candidate.CompositeCost)
            .FirstOrDefault();
        if (selected is null
            || MapStructureValidator.ValidateAbsolute(selected, tuning)
                != MapStructureRejectionReason.None)
        {
            return false;
        }

        var minimumProjectionCorrelation = Math.Max(
            0.55d,
            tuning.LowStructureMinimumProjectionCorrelation * 0.70d);
        return selected.ReferenceCoverage
                >= tuning.LowStructureMinimumReferenceCoverage
            && selected.ProjectionCorrelation >= minimumProjectionCorrelation;
    }

    private static MapVpsgScaleEstimate? TryEstimateLowStructureContentScale(
        MapCvRecognitionService service,
        MapRecord map,
        string floorKey,
        MapStructureFeatures preparedReference,
        MapStructureFeatures preparedLive,
        MapStructureRegistrationTuning structureTuning,
        MapScaleSearchPolicy scaleSearchPolicy,
        bool isTracking,
        MapScanDiagnostics diagnostics,
        ref MapOverlayTransform scaleSeed)
    {
        if (structureTuning.Channel != MapAlignmentChannel.LowStructure
            || !structureTuning.LowStructureEnableFeatureScaleEstimate
            || scaleSearchPolicy != MapScaleSearchPolicy.Search
            || isTracking)
        {
            return null;
        }

        diagnostics.ScaleBootstrapAttempted = true;
        var scaleGraph = service.VpsgScaleGraphCache.GetOrCreate(
            map,
            floorKey,
            preparedReference.Edges.Size(),
            preparedReference.KeyPoints);
        var scaleEstimated = service.VpsgScaleEstimator.TryEstimate(
            preparedReference,
            preparedLive,
            scaleGraph,
            scaleSeed.ScaleX,
            out var estimate,
            out var rejectionReason);
        diagnostics.ScaleBootstrapSucceeded = scaleEstimated;
        if (estimate is null)
        {
            MapLogCollector.Instance.Append(
                MapLogCategory.StructureRegistration,
                MapLogLevel.Info,
                $"低结构内容尺度估计未形成可靠答案 · floor={floorKey}",
                details: new()
                {
                    ["mapId"] = map.Id,
                    ["floor"] = floorKey,
                    ["rejection"] = rejectionReason,
                    ["liveKeyPoints"] = preparedLive.KeyPoints.Length,
                    ["referenceKeyPoints"] = preparedReference.KeyPoints.Length
                });
            return null;
        }

        scaleSeed = MapFeatureCacheRules.CreateScaleSeed(
            map,
            floorKey,
            estimate.Scale);
        diagnostics.ScaleBootstrapScale = estimate.Scale;
        diagnostics.ScaleBootstrapConfidence = estimate.Confidence;
        diagnostics.ScaleBootstrapUniqueMatches = estimate.Evidence.UniqueMatches;
        diagnostics.ScaleBootstrapPairVotes = estimate.Evidence.PairVotes;
        return estimate;
    }
}
/*
 * 文件职责：MapCvAlignmentService.Logging。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
