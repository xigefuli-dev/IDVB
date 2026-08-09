// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 运行时设置持久化抽象。管理 settings.json 中的
/// 校准区域、热键绑定、调优参数等。
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// 从磁盘加载运行时设置。若文件不存在或需要迁移，返回默认设置。
    /// </summary>
    Task</* MapRuntimeSettings */ object> LoadAsync();

    /// <summary>
    /// 将运行时设置写入磁盘。
    /// </summary>
    Task SaveAsync(object /* MapRuntimeSettings */ settings);
}
