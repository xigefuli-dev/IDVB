using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;

namespace IDVBuff.Features.Notifications;

/// <summary>
/// <see cref="IGameOverlayToast"/> 的宿主实现：发布前先按「当前前台游戏客户区」把
/// 通知窗定位到游戏窗口上，再走宿主统一的通知中心
/// （与本体 <c>SessionOrchestrator.ToggleMatchStateAsync</c> 里那条提示同一路径）。
///
/// 拿不到游戏客户区（例如游戏不是前台）时照样提示，但会清掉旧定位，
/// 让通知窗退回当前前台窗口，而不是沿用很久以前的坐标。
/// </summary>
public sealed class GameOverlayToast : IGameOverlayToast
{
    private readonly IGameWindowCapture _capture;

    public GameOverlayToast(IGameWindowCapture capture) =>
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));

    public void Notice(string message)
    {
        SyncGameBounds();
        OverlayNotificationCenter.Notice(message);
    }

    public void Warning(string message)
    {
        SyncGameBounds();
        OverlayNotificationCenter.Warning(message);
    }

    public void Error(string message)
    {
        SyncGameBounds();
        OverlayNotificationCenter.Error(message);
    }

    private void SyncGameBounds()
    {
        try
        {
            if (_capture.TryGetForegroundClientBounds(out var boundsObject, out _, out _)
                && boundsObject is MapScreenRect bounds
                && bounds.IsValid)
            {
                OverlayNotificationCenter.UpdateGameBounds(bounds);
                return;
            }
            // 拿不到游戏客户区时必须清掉旧的定位值，不能沿用上一次的矩形。
            // 旧逻辑失败就直接 return，通知窗口会继续用很久以前记下的坐标
            // （例如窗口化时的位置），表现就是提示跑到屏幕右上方、不再居中。
            // 清空后 OverlayNotificationWindow.GetTargetBounds 会退回当前前台窗口，
            // 拿到的是这一局的真实位置。
            OverlayNotificationCenter.UpdateGameBounds(default);
        }
        catch
        {
            // 定位失败不影响提示本身。
        }
    }
}
