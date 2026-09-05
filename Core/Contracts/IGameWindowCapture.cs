// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 游戏窗口截图抽象。封装前台窗口发现、客户区坐标测量和屏幕像素捕获。
/// </summary>
public interface IGameWindowCapture
{
    /// <summary>
    /// 尝试获取前台游戏窗口的客户区坐标。
    /// </summary>
    /// <param name="clientBounds">输出：客户区屏幕坐标矩形。</param>
    /// <param name="windowHandle">输出：原生窗口句柄。</param>
    /// <param name="failureReason">输出：失败原因描述。</param>
    /// <returns>成功返回 true，失败返回 false。</returns>
    bool TryGetForegroundClientBounds(
        out /* MapScreenRect */ object clientBounds,
        out IntPtr windowHandle,
        out string failureReason);

    /// <summary>
    /// 截取前台游戏窗口的整个客户区。
    /// </summary>
    bool TryCaptureClient(
        out /* CapturedGameFrame? */ object? frame,
        out string failureReason);

    /// <summary>
    /// 截取前台游戏窗口中由归一化矩形指定的子区域（校准视口）。
    /// </summary>
    bool TryCaptureViewport(
        /* NormalizedRectangle */ object viewport,
        out /* CapturedGameFrame? */ object? frame,
        out string failureReason);

    /// <summary>
    /// Tries to acquire a frame captured by an existing capture operation.
    /// Implementations without a hot frame return false and do not capture.
    /// </summary>
    bool TryAcquireLatestViewportFrame(
        object viewport,
        TimeSpan maximumAge,
        out object? frame)
    {
        frame = null;
        return false;
    }
}
