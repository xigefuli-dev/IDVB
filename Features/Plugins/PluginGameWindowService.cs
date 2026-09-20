using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 将宿主已验证的游戏客户区暴露给 PluginSDK。实际窗口筛选仍由
/// <see cref="IGameWindowCapture"/> 完成，插件只拿到不可变的坐标快照。
/// </summary>
public sealed class PluginGameWindowService : IPluginGameWindowService
{
    private readonly IGameWindowCapture _capture;

    public PluginGameWindowService(IGameWindowCapture capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public bool TryGetForegroundClientBounds(
        out PluginClientBounds clientBounds,
        out IntPtr windowHandle,
        out string failureReason)
    {
        clientBounds = default;
        windowHandle = IntPtr.Zero;
        if (!_capture.TryGetForegroundClientBounds(
                out var boundsObject,
                out windowHandle,
                out failureReason)
            || boundsObject is not IDVBuff.Features.Maps.MapScreenRect bounds
            || !bounds.IsValid)
        {
            clientBounds = default;
            return false;
        }

        clientBounds = new PluginClientBounds(
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height));
        if (clientBounds.IsValid)
            return true;

        failureReason = "宿主返回了无效的游戏客户区。";
        clientBounds = default;
        windowHandle = IntPtr.Zero;
        return false;
    }
}
