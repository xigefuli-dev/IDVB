using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapOverlayBitmapRendererTests
{
    [Fact]
    public void Render_EmptySceneIsFullyTransparent()
    {
        using var bitmap = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
            24,
            18,
            DisplayTestMatrix.Baseline.Dpi,
            Map: null,
            Status: null,
            ShowStatus: false));

        Assert.Equal(PixelFormat.Format32bppPArgb, bitmap.PixelFormat);
        Assert.Equal(0, bitmap.GetPixel(12, 9).A);
    }

    [Fact]
    public void Render_MapUsesGlobalOpacityAndPhysicalPixelOffset()
    {
        var imagePath = CreateSolidImage(Color.Red);
        try
        {
            var map = new MapOverlayRenderMap(
                imagePath,
                Left: 7,
                Top: 5,
                Width: 12,
                Height: 12,
                Anchors: []);
            using var bitmap = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
                30,
                24,
                DisplayTestMatrix.Baseline.Dpi,
                map,
                Status: null,
                ShowStatus: false));

            Assert.Equal(0, bitmap.GetPixel(3, 3).A);
            Assert.InRange(bitmap.GetPixel(12, 10).A, 114, 120);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Theory]
    [MemberData(nameof(DisplayTestMatrix.All), MemberType = typeof(DisplayTestMatrix))]
    public void Render_StatusUsesFullPhysicalResolutionAndScalesWithDpi(
        string name,
        int pixelWidth,
        int pixelHeight,
        int scalePercent,
        uint dpi)
    {
        var profile = DisplayTestMatrix.From(
            name,
            pixelWidth,
            pixelHeight,
            scalePercent,
            dpi);
        var status = new MapOverlayStatus(
            MapOverlayStatusLevel.Success,
            "Ready",
            "Overlay is active.");
        using var bitmap = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
            profile.PixelWidth,
            profile.PixelHeight,
            profile.Dpi,
            Map: null,
            status,
            ShowStatus: true));

        var origin = (int)Math.Round(12d * profile.ScaleFactor);
        var inside = (int)Math.Round(18d * profile.ScaleFactor);
        Assert.Equal(profile.PixelWidth, bitmap.Width);
        Assert.Equal(profile.PixelHeight, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(Math.Max(0, origin - 2), Math.Max(0, origin - 2)).A);
        Assert.True(bitmap.GetPixel(inside, inside).A > 0);
        Assert.Equal(0, bitmap.GetPixel(profile.PixelWidth - 1, profile.PixelHeight - 1).A);
    }

    [Fact]
    public void Render_MapIsStrictlyClippedToNativeViewport()
    {
        var imagePath = CreateSolidImage(Color.Red);
        try
        {
            var map = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 40,
                Height: 30,
                Anchors: [],
                ClipBounds: new MapScreenRect(10, 8, 15, 12));
            using var bitmap = MapOverlayBitmapRenderer.Render(
                new MapOverlayRenderScene(
                    40,
                    30,
                    DisplayTestMatrix.Baseline.Dpi,
                    map,
                    Status: null,
                    ShowStatus: false));

            Assert.Equal(0, bitmap.GetPixel(9, 14).A);
            Assert.True(bitmap.GetPixel(12, 14).A > 0);
            Assert.Equal(0, bitmap.GetPixel(26, 14).A);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void Render_PlayerIsDynamicAndCannotEscapeViewportClip()
    {
        var imagePath = CreateSolidImage(Color.Transparent);
        try
        {
            var map = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 40,
                Height: 30,
                Anchors: [],
                ClipBounds: new MapScreenRect(10, 8, 15, 12));
            using var inside = MapOverlayBitmapRenderer.Render(
                new MapOverlayRenderScene(
                    40,
                    30,
                    DisplayTestMatrix.Baseline.Dpi,
                    map,
                    Status: null,
                    ShowStatus: false,
                    Player: CreatePlayer(17, 14)));
            using var outside = MapOverlayBitmapRenderer.Render(
                new MapOverlayRenderScene(
                    40,
                    30,
                    DisplayTestMatrix.Baseline.Dpi,
                    map,
                    Status: null,
                    ShowStatus: false,
                    Player: CreatePlayer(30, 24)));

            Assert.True(inside.GetPixel(17, 14).A > 0);
            Assert.Equal(0, outside.GetPixel(30, 24).A);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void PersistentMiniMapSize_UsesActualImageAspectRatio()
    {
        var imagePath = CreateSolidImage(Color.Red, 40, 20);
        try
        {
            Assert.True(
                MapOverlayBitmapRenderer.TryGetScaledImageSize(
                    imagePath,
                    0.5d,
                    out var width,
                    out var height));

            Assert.Equal(20f, width);
            Assert.Equal(10f, height);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void Render_MiniMapPreservesNonSquareImageAspectRatio()
    {
        var imagePath = CreateSolidImage(Color.Red, 40, 20);
        try
        {
            var miniMap = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 40,
                Height: 20,
                Anchors: []);
            using var bitmap = MapOverlayBitmapRenderer.Render(
                new MapOverlayRenderScene(
                    100,
                    80,
                    DisplayTestMatrix.Baseline.Dpi,
                    Map: null,
                    Status: null,
                    ShowStatus: false,
                    MiniMap: miniMap,
                    GameScreenBounds: new MapScreenRect(0, 0, 100, 80),
                    MonitorWorkingArea: new MapScreenRect(0, 0, 1000, 1000),
                    MiniMapOffsetY: 0));

            Assert.True(bitmap.GetPixel(30, 20).A > 0);
            Assert.Equal(0, bitmap.GetPixel(30, 35).A);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void Render_MiniMapCanShowFloorLabelAtTopLeft()
    {
        var imagePath = CreateSolidImage(Color.Red, 40, 20);
        try
        {
            var miniMap = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 40,
                Height: 20,
                Anchors: [],
                FloorLabel: "1F");
            using var bitmap = MapOverlayBitmapRenderer.Render(
                new MapOverlayRenderScene(
                    100,
                    80,
                    DisplayTestMatrix.Baseline.Dpi,
                    Map: null,
                    Status: null,
                    ShowStatus: false,
                    MiniMap: miniMap,
                    GameScreenBounds: new MapScreenRect(0, 0, 100, 80),
                    MonitorWorkingArea: new MapScreenRect(0, 0, 1000, 1000),
                    MiniMapOffsetY: 0,
                    ShowFloorOnMiniMap: true));

            var hasWhiteLabelPixel = false;
            for (var y = 14; y < 32 && !hasWhiteLabelPixel; y++)
            {
                for (var x = 16; x < 45; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.A > 0 && pixel.R > 180 && pixel.G > 180 && pixel.B > 180)
                    {
                        hasWhiteLabelPixel = true;
                        break;
                    }
                }
            }

            Assert.True(hasWhiteLabelPixel);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void Render_PlayerUsesTheSelectedSlotImage()
    {
        var path = MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player2);
        using var source = new Bitmap(path);
        using var bitmap = MapOverlayBitmapRenderer.Render(
            new MapOverlayRenderScene(
                120,
                100,
                DisplayTestMatrix.Baseline.Dpi,
                Map: null,
                Status: null,
                ShowStatus: false,
                Player: new MapOverlayRenderPlayer(
                    PlayerSlot.Player2,
                    path,
                    60,
                    50,
                    source.Width,
                    source.Height,
                    0.95f)));

        var expected = source.GetPixel(source.Width / 2, source.Height / 2);
        var actual = bitmap.GetPixel(60, 50);
        Assert.Equal(expected.R, actual.R);
        Assert.Equal(expected.G, actual.G);
        Assert.Equal(expected.B, actual.B);
        Assert.True(actual.A > 0);
    }

    [Fact]
    public void AnchorColor_UsesStableSemanticPalette()
    {
        Assert.Equal(
            Color.FromArgb(255, 38, 133, 255),
            MapOverlayBitmapRenderer.AnchorColor("main-entrance"));
        Assert.Equal(
            Color.FromArgb(255, 236, 150, 61),
            MapOverlayBitmapRenderer.AnchorColor("custom"));
    }

    private static string CreateSolidImage(Color color, int width = 12, int height = 12)
    {
        var path = Path.Combine(Path.GetTempPath(), $"idvbuff-overlay-{Guid.NewGuid():N}.png");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static MapOverlayRenderPlayer CreatePlayer(float x, float y) =>
        new(
            PlayerSlot.Player1,
            MapPlayerAssetCatalog.ResolvePath(PlayerSlot.Player1),
            x,
            y,
            12,
            12,
            0.9f);
}
