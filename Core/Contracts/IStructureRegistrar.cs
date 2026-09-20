// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 结构配准抽象。将实时视口的边缘/结构通过 Chamfer 匹配、
/// ORB 特征投票、金字塔搜索等策略配准到参考地图的结构上。
/// 只允许均匀缩放 + 平移的刚性变换。
/// </summary>
public interface IStructureRegistrar
{
    /// <summary>
    /// 执行结构配准：在参考图像上搜索实时视口的最佳对齐位置和缩放。
    /// </summary>
    /// <param name="request">包含参考图、实时 ROI、锁定变换、调优参数等的完整请求。</param>
    /// <returns>包含是否接受、变换、置信度、候选列表、详细计时等的结果。</returns>
    object /* MapStructureRegistrationResult */ Register(
        object /* MapStructureRegistrationRequest */ request);
}
