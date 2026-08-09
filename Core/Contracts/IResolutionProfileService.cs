// IDVB Remaster Phase 0.3 — Core Contract

using IDVBuff.Core.Models;

namespace IDVBuff.Core.Contracts;

/// <summary>
/// 分辨率预设管理与切换服务。
/// </summary>
public interface IResolutionProfileService
{
    /// <summary>
    /// 返回所有可用的分辨率调优预设。
    /// </summary>
    IReadOnlyList<ResolutionTuningProfile> GetAvailableProfiles();

    /// <summary>
    /// 切换到指定名称的分辨率预设，并应用对应的调优参数覆盖。
    /// </summary>
    Task SetActiveProfileAsync(string profileName);

    /// <summary>
    /// 分辨率预设切换后触发。
    /// </summary>
    event EventHandler? ResolutionChanged;
}
