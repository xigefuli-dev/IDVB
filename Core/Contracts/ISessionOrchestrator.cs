// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 对局会话编排器抽象。管理从地图打开到锁定识别的完整生命周期，
/// 编排门检测、楼层识别、几何排名、结构配准、叠加渲染等管线阶段。
/// </summary>
public interface ISessionOrchestrator
{
    /// <summary>
    /// 初始化服务：加载设置、预热识别缓存、注册输入绑定。
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// 开始对局：选择地图 Class 并启动持续扫描。
    /// </summary>
    Task BeginMatchAsync();

    /// <summary>
    /// 结束对局：停止扫描、清除叠加内容。
    /// </summary>
    Task EndMatchAsync();

    /// <summary>
    /// 执行一次完整的快速扫描：截帧 → 门检测 → 几何排名 → 结构配准 → 锁定。
    /// </summary>
    Task RunScanAsync();

    /// <summary>
    /// 会话状态变更事件。每次地图识别状态发生变化时触发。
    /// </summary>
    event EventHandler? StateChanged;
}
