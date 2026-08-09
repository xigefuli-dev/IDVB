// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 地图对齐策略接口（策略模式）。每种对齐策略对应一种识别管线路径
/// （如双门几何、单门跟踪、辅助锚点、结构配准等），由编排器按优先级选取。
/// </summary>
public interface IMapAligner
{
    /// <summary>
    /// 执行对齐操作，返回对齐结果。
    /// </summary>
    /// <param name="map">目标地图记录。</param>
    /// <param name="screenshot">实时截帧数据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>包含变换、置信度、来源等的对齐结果。</returns>
    Task</* AlignmentResult */ object> AlignAsync(
        object /* MapRecord */ map,
        object /* Mat */ screenshot,
        CancellationToken ct);

    /// <summary>
    /// 策略名称，用于诊断日志和优先级排序。
    /// </summary>
    string StrategyName { get; }
}
