// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 地图身份识别抽象。将门检测结果与所有已注册地图的几何指纹逐一比对，
/// 按向量误差排名确定最可能的地图。
/// </summary>
public interface IMapIdentifier
{
    /// <summary>
    /// 对门候选与所有地图指纹进行几何排名。
    /// </summary>
    /// <param name="fingerprints">所有已注册地图的几何指纹列表。</param>
    /// <param name="gates">检测到的门候选列表。</param>
    /// <param name="viewportBounds">视口屏幕坐标。</param>
    /// <param name="vectorErrorTolerance">向量误差阈值。</param>
    /// <param name="testSwappedAssignments">是否测试大门/侧门的互换指派。</param>
    /// <returns>按向量误差升序排列的几何候选列表。</returns>
    IReadOnlyList</* MapGeometryCandidate */ object> RankGeometry(
        IReadOnlyList</* MapGeometryFingerprint */ object> fingerprints,
        IReadOnlyList</* GateDetection */ object> gates,
        object /* MapScreenRect */ viewportBounds,
        double vectorErrorTolerance = 0.12d,
        bool testSwappedAssignments = true);
}
