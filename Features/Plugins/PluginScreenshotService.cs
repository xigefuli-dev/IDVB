using System.Diagnostics;
using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using OpenCvSharp;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 宿主对 PluginSDK 进程截图能力的适配。截图仍沿用主程序现有的前台窗口校验和
/// 客户区抓取逻辑，因此插件无法读取任意窗口或在后台抓取被监测进程。
/// </summary>
public sealed class PluginScreenshotService : IPluginScreenshotService
{
    private readonly IGameWindowCapture _capture;

    public PluginScreenshotService(IGameWindowCapture capture)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    }

    public async Task<PluginScreenshotResult> CaptureAsync(
        TimeSpan? switchDelay = null,
        CancellationToken cancellationToken = default)
    {
        var delay = switchDelay ?? PluginScreenshotDefaults.SwitchDelay;
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(switchDelay));

        var stopwatch = Stopwatch.StartNew();
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_capture.TryCaptureClient(out var frameObject, out var failureReason)
                || frameObject is not Features.Maps.CapturedGameFrame frame)
            {
                if (frameObject is IDisposable disposable)
                    disposable.Dispose();

                return PluginScreenshotResult.Failure(
                    string.IsNullOrWhiteSpace(failureReason)
                        ? "无法获取被监测进程的画面。"
                        : failureReason,
                    stopwatch.Elapsed);
            }

            using (frame)
            {
                if (frame.Image.Empty())
                {
                    return PluginScreenshotResult.Failure(
                        "被监测进程返回了空截图。",
                        stopwatch.Elapsed);
                }

                if (!Cv2.ImEncode(".png", frame.Image, out var imageBytes)
                    || imageBytes.Length == 0)
                {
                    return PluginScreenshotResult.Failure(
                        "无法编码被监测进程的截图。",
                        stopwatch.Elapsed);
                }

                var screenshot = new PluginScreenshot(
                    imageBytes,
                    frame.Image.Width,
                    frame.Image.Height,
                    DateTimeOffset.UtcNow);
                return PluginScreenshotResult.Success(
                    screenshot,
                    stopwatch.Elapsed);
            }
        }
        catch (Exception exception)
        {
            return PluginScreenshotResult.Failure(
                $"获取被监测进程截图失败：{exception.Message}",
                stopwatch.Elapsed);
        }
    }
}
