// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 门图标模板匹配检测器抽象。
/// 在一次截帧上搜索游戏地图的两个门图标（大门 + 侧门）。
/// </summary>
public interface IGateDetector : IDisposable
{
    /// <summary>
    /// 简化的门检测：对整个视口执行全尺度搜索。
    /// </summary>
    /// <param name="liveMatchImage">预处理后的实时匹配图像。</param>
    /// <param name="viewportBounds">视口屏幕坐标。</param>
    /// <param name="clientWidth">游戏窗口客户区宽度（像素）。</param>
    /// <param name="scoreThreshold">模板匹配得分阈值（0-1）。</param>
    /// <returns>检测到的门候选列表。</returns>
    IReadOnlyList</* GateDetection */ object> Detect(
        object /* Mat */ liveMatchImage,
        object /* MapScreenRect */ viewportBounds,
        double clientWidth = 1920d,
        double scoreThreshold = 0.6d);

    /// <summary>
    /// 全参门检测：支持搜索模式、预算、搜索上下文等。
    /// </summary>
    /// <param name="liveMatchImage">预处理后的实时匹配图像。</param>
    /// <param name="viewportBounds">视口屏幕坐标。</param>
    /// <param name="clientWidth">游戏窗口客户区宽度（像素）。</param>
    /// <param name="scoreThreshold">模板匹配得分阈值（0-1）。</param>
    /// <param name="searchContext">搜索上下文（模式、预算、ROI 等）。为 null 时等价于 FullSearch。</param>
    /// <returns>完整的门检测诊断结果，包含门列表、原始候选、耗时等。</returns>
    object /* GateDetectionResult */ Detect(
        object /* Mat */ liveMatchImage,
        object /* MapScreenRect */ viewportBounds,
        double clientWidth,
        double scoreThreshold,
        object? /* GateSearchContext? */ searchContext);

    /// <summary>
    /// 是否存在已记忆的温标（上次成功检测的 scale）。
    /// </summary>
    bool HasWarmScale { get; }

    /// <summary>
    /// 已记忆的温标值（上次成功检测的 scale），可能为 null。
    /// </summary>
    double? WarmScale { get; }

    /// <summary>
    /// 记录成功检测的 scale 作为温标，加速后续搜索。
    /// </summary>
    void RememberSuccessfulScale(double scale);

    /// <summary>
    /// 清除已记忆的温标，回到冷启动状态。
    /// </summary>
    void ResetSuccessfulScale();
}
