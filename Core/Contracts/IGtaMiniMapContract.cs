// IDVB Enhanced MiniMap — GTA5 Style Local MiniMap Contracts

namespace IDVBuff.Core.Contracts;

/// <summary>
/// GTA5 风格小地图中玩家标记的视觉形态。
/// </summary>
public enum GtaPlayerTrackingVisualKind
{
    /// <summary>实时锁定状态：显示为十字箭头/准星标。</summary>
    Crosshair = 0,

    /// <summary>失去实时位置：从十字标转变为逐渐扩大的范围扩散圈。</summary>
    ExpandingRing = 1
}

/// <summary>
/// GTA5 风格玩家追踪状态模型。
/// </summary>
public sealed class GtaPlayerTrackingState
{
    public object PlayerSlot { get; init; } = default!;
    public GtaPlayerTrackingVisualKind VisualKind { get; set; } = GtaPlayerTrackingVisualKind.Crosshair;
    public double NormalizedX { get; set; }
    public double NormalizedY { get; set; }
    public DateTimeOffset LastSeenTimestamp { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LostTimestamp { get; set; }

    /// <summary>
    /// 当失去玩家实时位置时，根据流逝时间动态计算当前扩散圈半径。
    /// </summary>
    public double GetExpandingRadius(
        DateTimeOffset now,
        double baseRadius = 8.0,
        double maxRadius = 45.0,
        double growthRatePerSecond = 12.0)
    {
        if (VisualKind != GtaPlayerTrackingVisualKind.ExpandingRing || LostTimestamp is null)
            return baseRadius;

        var elapsedSeconds = Math.Max(0.0, (now - LostTimestamp.Value).TotalSeconds);
        return Math.Min(maxRadius, baseRadius + (elapsedSeconds * growthRatePerSecond));
    }
}

/// <summary>
/// GTA5 风格以玩家为中心的局部小地图视口参数。
/// </summary>
public sealed record GtaMiniMapViewport(
    double CenterNormalizedX,
    double CenterNormalizedY,
    double ViewportRadiusNormalized,
    double RotationDegrees);

/// <summary>
/// GTA5 风格局部小地图架构契约接口（为后续现代 UI 渲染与局部裁剪预留）。
/// </summary>
public interface IGtaMiniMapContract
{
    /// <summary>更新指定玩家槽位的追踪状态。</summary>
    void UpdateTrackingState(object playerSlot, GtaPlayerTrackingState state);

    /// <summary>标记指定玩家槽位失去实时追踪（触发十字箭头向扩散圆圈转换）。</summary>
    void MarkTrackingLost(object playerSlot, DateTimeOffset lostAt);

    /// <summary>重新恢复锁定指定玩家位置（立即恢复为十字标）。</summary>
    void RestoreTrackingLocked(object playerSlot, double normalizedX, double normalizedY, DateTimeOffset acquiredAt);

    /// <summary>设置局部小地图的以玩家为中心的视口参数。</summary>
    void SetLocalViewport(GtaMiniMapViewport viewport);

    /// <summary>获取当前所有活跃追踪的玩家状态。</summary>
    IReadOnlyList<GtaPlayerTrackingState> GetActiveTrackingStates();
}
