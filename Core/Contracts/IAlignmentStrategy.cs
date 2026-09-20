// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 对齐策略 marker 接口。比 IMapAligner 更细粒度，
/// 表示单一的对齐手段（如双门几何对齐、单门平移跟踪、结构配准等），
/// 供策略链/优先级选择器使用。
/// </summary>
/// <remarks>
/// 该接口暂不定义方法成员。在 Phase 0.4 中，
/// 其精确的方法签名将根据 MapRuntimeService 中实际的对齐流程
/// （AlignSelected、AlignSideEntrance、ConfirmSelectedAlignment 等）来细化。
/// </remarks>
public interface IAlignmentStrategy
{
    // Phase 0.4 将定义具体的方法签名，例如：
    // Task<AlignmentResult> ExecuteAsync(AlignmentContext context, CancellationToken ct);
    // string Name { get; }
    // int Priority { get; }
    // bool CanHandle(AlignmentContext context);
}
