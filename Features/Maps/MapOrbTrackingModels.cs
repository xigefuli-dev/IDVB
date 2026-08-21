using IDVBuff.Core.Models;

namespace IDVBuff.Features.Maps;

public sealed class MapOrbTrackingOptions
{
    public int FeatureCount { get; init; } = 1200;
    public double RatioThreshold { get; init; } = 0.64;
    public int MinimumMatches { get; init; } = 12;
    public int MinimumRansacInliers { get; init; } = 8;
    public double MinimumInlierRatio { get; init; } = 0.50;
    public double MaximumMedianReprojectionErrorPixels { get; init; } = 2.5;
    public double MaximumRotationDegrees { get; init; } = 0.5;
    public double MaximumStepScaleChangeRatio { get; init; } = 0.01;
    public double MaximumBaselineScaleChangeRatio { get; init; } = 0.03;
    public double MinimumTranslationLimitPixels { get; init; } = 24;
    public double MaximumTranslationPixelsPerSecond { get; init; } = 600;
    public double TranslationDeadbandPixels { get; init; } = 0.5;
    public double ScaleDeadbandRatio { get; init; } = 0.0005;
    public IReadOnlyList<NormalizedRectangle> IgnoreRegions { get; init; } = [];

    public static MapOrbTrackingOptions FromConfig(
        OrbTrackingConfig config,
        IReadOnlyList<NormalizedRectangle>? ignoreRegions = null) => new()
    {
        FeatureCount = Math.Clamp(config.FeatureCount, 100, 10000),
        RatioThreshold = Math.Clamp(config.RatioThreshold, 0.1, 0.99),
        MinimumMatches = Math.Max(4, config.MinimumMatches),
        MinimumRansacInliers = Math.Max(3, config.MinimumRansacInliers),
        MinimumInlierRatio = Math.Clamp(config.MinimumInlierRatio, 0.1, 1),
        MaximumMedianReprojectionErrorPixels = Math.Max(0.1, config.MaximumMedianReprojectionErrorPixels),
        MaximumRotationDegrees = Math.Clamp(config.MaximumRotationDegrees, 0, 30),
        MaximumStepScaleChangeRatio = Math.Clamp(config.MaximumStepScaleChangeRatio, 0, 0.25),
        MaximumBaselineScaleChangeRatio = Math.Clamp(config.MaximumBaselineScaleChangeRatio, 0, 0.50),
        MinimumTranslationLimitPixels = Math.Max(0, config.MinimumTranslationLimitPixels),
        MaximumTranslationPixelsPerSecond = Math.Max(0, config.MaximumTranslationPixelsPerSecond),
        TranslationDeadbandPixels = Math.Max(0, config.TranslationDeadbandPixels),
        ScaleDeadbandRatio = Math.Max(0, config.ScaleDeadbandRatio),
        IgnoreRegions = ignoreRegions ?? []
    };
}

public sealed record MapOrbTrackingResult(
    bool Accepted,
    bool ShouldCommit,
    MapOverlayTransform Transform,
    string RejectionReason,
    int MatchCount,
    int InlierCount,
    double InlierRatio,
    double MedianReprojectionErrorPixels,
    double EstimatedRotationDegrees,
    double StepScale,
    double TranslationPixels,
    double FeatureExtractionMilliseconds = 0,
    double MatchingMilliseconds = 0,
    double RansacMilliseconds = 0)
{
    public static MapOrbTrackingResult Reject(
        MapOverlayTransform transform,
        string reason,
        int matches = 0,
        int inliers = 0,
        double inlierRatio = 0,
        double error = double.PositiveInfinity,
        double rotation = 0,
        double stepScale = 1,
        double translation = 0) => new(
            false, false, transform, reason, matches, inliers, inlierRatio,
            error, rotation, stepScale, translation);
}
/*
 * 文件职责：MapOrbTrackingModels。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
