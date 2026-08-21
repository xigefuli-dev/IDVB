using IDVBuff.Diagnostics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;

namespace IDVBuff;

/// <summary>
/// 平滑的高斯模糊窗口背景。
/// 系统 Desktop Acrylic 的模糊半径固定且偏小，桌面/后方窗口的高对比内容
/// 会以软边补丁的形式透出来，产生"块状破碎感"；这里改用自定义合成背景：
/// GaussianBlurEffect 作用于窗口背后内容（CreateHostBackdropBrush），把
/// 一切均匀打散成连续的模糊。色调仍由 XAML 窗口背景（FluentTheme.WindowBrush）
/// 叠加，跟随当前主题。
/// </summary>
internal sealed class GaussianBlurBackdrop : SystemBackdrop
{
    // ── 可调旋钮 ──────────────────────────────────────────────
    // BlurAmount 是高斯模糊半径（DIP，设备无关像素）：
    //   20 左右 → 轻度柔焦，仍能看出轮廓
    //   40 左右 → 均匀打散的连续模糊（推荐）
    //   60+    → 接近纯色，几乎只剩色彩氛围
    private const float BlurAmount = 42f;

    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionBrush? _backdrop;

    // Windows.UI.Composition.Compositor 的激活要求当前线程已挂载
    // Windows.System.DispatcherQueue，而 OnTargetConnected 可能跑在没有该队列的
    // 线程上；需要先经原生 CreateDispatcherQueueController 补一个并持有句柄防止
    // 被回收（WinUIEx 的 WindowManager.Compositor 同款做法——托管版
    // DispatcherQueueController.CreateOnCurrentThread 创建的是 Microsoft 投影的
    // 队列，不满足 Compositor 的激活检查）。
    private static IntPtr _dispatcherQueueControllerHandle;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        internal int DwSize;
        internal int ThreadType;
        internal int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(
        [In] DispatcherQueueOptions options,
        out IntPtr dispatcherQueueController);

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget,
        XamlRoot xamlRoot)
    {
        OutputLog.Write("INFO", "BACKDROP", "OnTargetConnected entered.");
        try
        {
            base.OnTargetConnected(connectedTarget, xamlRoot);
            OutputLog.Write("INFO", "BACKDROP", "base.OnTargetConnected OK.");

            // 接口的 SystemBackdrop 属性是 Windows.UI.Composition 投影，只能用
            // 同一投影的 compositor/画刷才能赋上去（WinUIEx 同款做法）。
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null
                && _dispatcherQueueControllerHandle == IntPtr.Zero)
            {
                var options = new DispatcherQueueOptions
                {
                    DwSize = Marshal.SizeOf(typeof(DispatcherQueueOptions)),
                    ThreadType = 2,    // DQTYPE_THREAD_CURRENT
                    ApartmentType = 2  // DQTAT_COM_STA
                };
                _ = CreateDispatcherQueueController(options, out _dispatcherQueueControllerHandle);
            }
            var compositor = new Windows.UI.Composition.Compositor();
            _compositor = compositor;
            OutputLog.Write("INFO", "BACKDROP", "Windows compositor created.");

            var effect = new GaussianBlurEffect
            {
                Name = "WindowBackdropBlur",
                BlurAmount = BlurAmount,
                BorderMode = EffectBorderMode.Soft,
                Source = new Windows.UI.Composition.CompositionEffectSourceParameter("source")
            };
            var factory = compositor.CreateEffectFactory(effect);
            var brush = factory.CreateBrush();
            brush.SetSourceParameter("source", compositor.CreateHostBackdropBrush());
            OutputLog.Write("INFO", "BACKDROP", "Blur effect brush built.");

            _backdrop = brush;
            connectedTarget.SystemBackdrop = brush;
            OutputLog.Write(
                "INFO",
                "BACKDROP",
                $"Gaussian blur backdrop attached (BlurAmount={BlurAmount}).");
        }
        catch (Exception exception)
        {
            // 模糊不可用时退化为纯色——XAML 窗口背景（FluentTheme.WindowBrush）
            // 依然存在，不会白屏。
            OutputLog.Write(
                "ERROR",
                "BACKDROP",
                "Gaussian blur backdrop failed; falling back to solid window background.",
                exception);
            _backdrop?.Dispose();
            _backdrop = null;
            _compositor?.Dispose();
            _compositor = null;
        }
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            if (disconnectedTarget.SystemBackdrop is not null)
                disconnectedTarget.SystemBackdrop = null;
        }
        finally
        {
            _backdrop?.Dispose();
            _backdrop = null;
            _compositor?.Dispose();
            _compositor = null;
        }
        base.OnTargetDisconnected(disconnectedTarget);
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(
        ICompositionSupportsSystemBackdrop target,
        XamlRoot xamlRoot)
    {
        // 自绘背景不依赖系统主题配置；基类默认实现会对自绘 Backdrop 抛
        // ArgumentException（历史崩溃根因），保持空实现即可。
    }
}
