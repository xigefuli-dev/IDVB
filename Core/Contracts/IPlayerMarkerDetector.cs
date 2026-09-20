// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 玩家标记检测器抽象。在已锁定的地图视口中检测指定玩家的标记图标。
/// </summary>
public interface IPlayerMarkerDetector : IDisposable
{
    /// <summary>
    /// 在实时视口图像中检测指定玩家的标记。
    /// </summary>
    /// <param name="liveViewport">实时视口截图。</param>
    /// <param name="viewportBounds">视口屏幕坐标。</param>
    /// <param name="clientBounds">窗口客户区屏幕坐标。</param>
    /// <param name="playerSlot">玩家槽位。</param>
    /// <param name="templatePath">玩家图标模板文件路径。</param>
    /// <param name="previousPoint">上次检测到的玩家位置（用于局部搜索加速），可能为 null。</param>
    /// <param name="tuning">追踪调优参数，为 null 则使用默认值。</param>
    /// <returns>包含是否成功、视口坐标、屏幕坐标、置信度等的检测结果。</returns>
    object /* MapPlayerMarkerDetection */ Detect(
        object /* Mat */ liveViewport,
        object /* MapScreenRect */ viewportBounds,
        object /* MapScreenRect */ clientBounds,
        /* PlayerSlot */ object playerSlot,
        string templatePath,
        object? /* MapViewportPoint? */ previousPoint,
        object? /* MapPlayerTrackingTuning? */ tuning = null);

    /// <summary>
    /// 重置追踪状态，清除连续失败计数。
    /// </summary>
    void ResetTracking();
}
