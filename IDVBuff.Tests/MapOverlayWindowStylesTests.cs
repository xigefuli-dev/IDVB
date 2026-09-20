using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOverlayWindowStylesTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(MapOverlayWindowStyles.Layered)]
    [InlineData(MapOverlayWindowStyles.NoRedirectionBitmap)]
    [InlineData(MapOverlayWindowStyles.Layered | MapOverlayWindowStyles.NoRedirectionBitmap)]
    public void Create_SelectsLayeredRenderingAndRequiredInputStyles(long currentStyles)
    {
        var result = MapOverlayWindowStyles.Create(currentStyles);

        Assert.True(MapOverlayWindowStyles.AreApplied(result));
        Assert.NotEqual(0, result & MapOverlayWindowStyles.Layered);
        Assert.Equal(0, result & MapOverlayWindowStyles.NoRedirectionBitmap);
    }

    [Fact]
    public void AreApplied_RejectsIncompleteInputStyles()
    {
        Assert.False(MapOverlayWindowStyles.AreApplied(MapOverlayWindowStyles.Transparent));
        Assert.False(MapOverlayWindowStyles.AreApplied(
            MapOverlayWindowStyles.Transparent | MapOverlayWindowStyles.ToolWindow));
        Assert.False(MapOverlayWindowStyles.AreApplied(
            MapOverlayWindowStyles.Required | MapOverlayWindowStyles.NoRedirectionBitmap));
    }

    [Fact]
    public void CaptureProtection_DefaultsAllCategoriesToUnprotected()
    {
        using var service = new IDVBuff.Features.Capture.WindowCaptureProtectionService();

        Assert.False(service.IsPluginEnabled);
        Assert.False(service.IsProtectionRequested(IDVBuff.Core.Contracts.CaptureProtectionWindowCategory.MainProgram));
        Assert.False(service.IsProtectionRequested(IDVBuff.Core.Contracts.CaptureProtectionWindowCategory.DisplayLayer));
    }

    [Fact]
    public void CaptureProtection_RealNativeOverlayWindow_InheritsAndRestoresPolicy()
    {
        using var service = new IDVBuff.Features.Capture.WindowCaptureProtectionService();
        using var overlayWindow = new MapOverlayNativeWindow(service);
        using var bitmap = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        overlayWindow.Present(bitmap, new MapScreenRect(-30000, -30000, 2, 2));

        Assert.NotEqual(IntPtr.Zero, overlayWindow.Handle);
        // 默认状态：插件未启用，显示层捕获排除为 false
        Assert.False(overlayWindow.IsCaptureExclusionEnabled);

        // 模拟直播模式开启且保持“隐藏显示层”：开启排除
        service.SetPolicy(pluginEnabled: true, hideMainProgram: false, hideDisplayLayer: true);
        Assert.True(overlayWindow.IsCaptureExclusionEnabled);

        // 模拟直播模式开启且用户取消“隐藏显示层”：允许显示层被录屏捕获
        service.SetPolicy(pluginEnabled: true, hideMainProgram: false, hideDisplayLayer: false);
        Assert.False(overlayWindow.IsCaptureExclusionEnabled);

        // 模拟直播模式关闭：捕获排除恢复为 false
        service.SetPolicy(pluginEnabled: false, hideMainProgram: false, hideDisplayLayer: false);
        Assert.False(overlayWindow.IsCaptureExclusionEnabled);

        overlayWindow.Hide();
    }

    [Fact]
    public void TrySetCaptureExclusion_DirectActivationAndLiveModeProtection()
    {
        using var service = new IDVBuff.Features.Capture.WindowCaptureProtectionService();
        using var overlayWindow = new MapOverlayNativeWindow(service);
        using var bitmap = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        overlayWindow.Present(bitmap, new MapScreenRect(-30000, -30000, 2, 2));

        // 默认状态：宿主服务存在且插件未启用，显式激活排除应被拒绝，显示层保持可捕获（保护录屏可用性）
        var success = overlayWindow.TrySetCaptureExclusion(true, out var failureReason);
        Assert.False(success);
        Assert.Contains("直播模式", failureReason);
        Assert.False(overlayWindow.IsCaptureExclusionEnabled);

        // 模拟直播模式开启且取消“隐藏显示层”：此时显式激活同样应被阻止以尊重直播间设置
        service.SetPolicy(pluginEnabled: true, hideMainProgram: false, hideDisplayLayer: false);
        Assert.False(overlayWindow.IsCaptureExclusionEnabled);
        var blocked = overlayWindow.TrySetCaptureExclusion(true, out var liveFailureReason);
        Assert.False(blocked);
        Assert.Contains("直播模式", liveFailureReason);
        Assert.False(overlayWindow.IsCaptureExclusionEnabled);

        // 模拟直播模式开启且启用“隐藏显示层”：显示层被排除捕获
        service.SetPolicy(pluginEnabled: true, hideMainProgram: false, hideDisplayLayer: true);
        Assert.True(overlayWindow.IsCaptureExclusionEnabled);

        overlayWindow.Hide();

        // 独立无宿主服务环境：显式激活排除直接控制底层句柄
        using var standaloneOverlay = new MapOverlayNativeWindow(null);
        using var standaloneBitmap = new System.Drawing.Bitmap(2, 2, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        standaloneOverlay.Present(standaloneBitmap, new MapScreenRect(-30000, -30000, 2, 2));

        var standaloneSuccess = standaloneOverlay.TrySetCaptureExclusion(true, out var standaloneReason);
        Assert.True(standaloneSuccess);
        Assert.Empty(standaloneReason);
        Assert.True(standaloneOverlay.IsCaptureExclusionEnabled);

        standaloneOverlay.Hide();
    }
}
