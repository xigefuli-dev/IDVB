using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOverlayNativeWindowTests
{
    [Fact]
    public void Present_CreatesLayeredClickThroughWindowThatCanBeHidden()
    {
        using var window = new MapOverlayNativeWindow();
        using var bitmap = new Bitmap(2, 2, PixelFormat.Format32bppPArgb);

        window.Present(bitmap, new MapScreenRect(-30000, -30000, 2, 2));

        Assert.NotEqual(IntPtr.Zero, window.Handle);
        Assert.True(window.IsVisible);
        var styles = GetWindowLongPtr(window.Handle, MapOverlayWindowStyles.GwlExStyle).ToInt64();
        Assert.True(MapOverlayWindowStyles.AreApplied(styles));

        window.Hide();

        Assert.False(window.IsVisible);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
