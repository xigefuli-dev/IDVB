// IDVB Real CLI — 基于文件的截图注入
// 实现 IGameWindowCapture，从静态图片文件提供截图，替代真实的 BitBlt 截屏。
// 这是 Real CLI 唯一"非真实"的组件——其余全部走 IDVB 原生管线。

using IDVBuff.Core.Contracts;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.RealCLI.Stubs;

/// <summary>
/// 文件截图注入器。从指定图片文件加载截图，模拟游戏窗口截屏。
/// 每次都重新从磁盘加载并转换为 BGR 格式，确保 Mat 绝对独立，
/// 避免 SessionOrchestrator 的 frame.Dispose() 影响后续访问。
/// </summary>
public sealed class FileBasedCapture : IGameWindowCapture
{
    private readonly string _imagePath;

    public FileBasedCapture(string imagePath)
    {
        _imagePath = imagePath;
    }

    private Mat LoadFresh()
    {
        var raw = Cv2.ImRead(_imagePath, ImreadModes.Color);
        if (raw.Empty())
            throw new InvalidOperationException($"无法加载截图：{_imagePath}");
        return raw;
    }

    /// <summary>所加载图片的完整尺寸。</summary>
    public MapScreenRect FullBounds
    {
        get
        {
            using var probe = Cv2.ImRead(_imagePath, ImreadModes.Color);
            return probe.Empty()
                ? default
                : new MapScreenRect(0, 0, probe.Width, probe.Height);
        }
    }

    public bool TryGetForegroundClientBounds(
        out object clientBounds,
        out IntPtr windowHandle,
        out string failureReason)
    {
        if (!File.Exists(_imagePath))
        {
            clientBounds = default(MapScreenRect);
            windowHandle = IntPtr.Zero;
            failureReason = $"文件不存在：{_imagePath}";
            return false;
        }

        clientBounds = FullBounds;
        windowHandle = new IntPtr(1);
        failureReason = string.Empty;
        return true;
    }

    public bool TryCaptureClient(out object? frame, out string failureReason)
    {
        try
        {
            var image = LoadFresh();
            var bounds = new MapScreenRect(0, 0, image.Width, image.Height);
            frame = new CapturedGameFrame(image, bounds, bounds, new IntPtr(1));
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            frame = null;
            failureReason = $"截图加载失败：{ex.Message}";
            return false;
        }
    }

    public bool TryCaptureViewport(
        object viewport,
        out object? frame,
        out string failureReason)
    {
        try
        {
            using var fullBgr = LoadFresh();

            if (viewport is not NormalizedRectangle normRect)
                return TryCaptureClient(out frame, out failureReason);

            var w = fullBgr.Width;
            var h = fullBgr.Height;

            var left = Math.Clamp((int)Math.Floor(normRect.X * w), 0, Math.Max(0, w - 1));
            var top = Math.Clamp((int)Math.Floor(normRect.Y * h), 0, Math.Max(0, h - 1));
            var right = Math.Clamp((int)Math.Ceiling((normRect.X + normRect.Width) * w), left + 1, w);
            var bottom = Math.Clamp((int)Math.Ceiling((normRect.Y + normRect.Height) * h), top + 1, h);

            if (right <= left || bottom <= top)
                return TryCaptureClient(out frame, out failureReason);

            // 使用 new Mat() + CopyTo 替代 Clone()，确保深拷贝
            var viewportBounds = new MapScreenRect(0, 0, right - left, bottom - top);
            var cropped = new Mat(fullBgr, new Rect(left, top, right - left, bottom - top));
            var independent = new Mat();
            cropped.CopyTo(independent);

            frame = new CapturedGameFrame(independent, FullBounds, viewportBounds, new IntPtr(1));
            failureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            frame = null;
            failureReason = $"视口截图加载失败：{ex.Message}";
            return false;
        }
    }
}
