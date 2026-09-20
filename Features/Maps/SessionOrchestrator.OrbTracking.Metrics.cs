using System.Diagnostics;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private void LogOrbMetrics(
        OrbTrackingContext context,
        string outcome,
        double captureMilliseconds,
        double orbMilliseconds,
        double structureMilliseconds,
        int weakFrames,
        string reason,
        MapOrbTrackingResult? result = null)
    {
        _logCollector.Append(
            MapLogCategory.OrbTracking,
            MapLogLevel.Info,
            $"ORB tracking sample · {outcome}",
            elapsedMs: captureMilliseconds + orbMilliseconds + structureMilliseconds,
            details: new()
            {
                ["generation"] = context.Generation,
                ["captureMs"] = captureMilliseconds,
                ["orbMs"] = orbMilliseconds,
                ["featureExtractionMs"] = result?.FeatureExtractionMilliseconds ?? 0,
                ["matchingMs"] = result?.MatchingMilliseconds ?? 0,
                ["ransacMs"] = result?.RansacMilliseconds ?? 0,
                ["structureCorrectionMs"] = structureMilliseconds,
                ["weakFrames"] = weakFrames,
                ["matches"] = result?.MatchCount ?? 0,
                ["inliers"] = result?.InlierCount ?? 0,
                ["inlierRatio"] = result?.InlierRatio ?? 0,
                ["medianReprojectionErrorPx"] = result?.MedianReprojectionErrorPixels,
                ["reason"] = reason
            });
    }

    private static double ElapsedMilliseconds(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * 1000d / Stopwatch.Frequency;
}
/*
 * 文件职责：SessionOrchestrator.OrbTracking.Metrics。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
