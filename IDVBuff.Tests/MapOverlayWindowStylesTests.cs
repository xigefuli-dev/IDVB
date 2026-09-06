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
}
