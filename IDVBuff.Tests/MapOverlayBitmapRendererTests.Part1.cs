using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapOverlayBitmapRendererTests
{
    [Fact]
    public void MapLayerCache_ReplacesPriorSizeForSameMapAndConfig()
    {
        var imagePath = CreateSolidImage(Color.Red, 40, 20);
        MapOverlayBitmapRenderer.InvalidateImageCache();
        try
        {
            var map1 = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 40,
                Height: 20,
                Anchors: []);
            using var bitmap1 = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
                50,
                30,
                DisplayTestMatrix.Baseline.Dpi,
                map1,
                Status: null,
                ShowStatus: false));

            Assert.Equal(1, MapOverlayBitmapRenderer.MapLayerCacheCount);

            var map2 = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 41,
                Height: 21,
                Anchors: []);
            using var bitmap2 = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
                50,
                30,
                DisplayTestMatrix.Baseline.Dpi,
                map2,
                Status: null,
                ShowStatus: false));

            // 尺寸改变后，旧尺寸的位图必须被淘汰并释放，条目数量必须保持为 1
            Assert.Equal(1, MapOverlayBitmapRenderer.MapLayerCacheCount);
        }
        finally
        {
            MapOverlayBitmapRenderer.InvalidateImageCache();
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void MiniMapLayerCache_ReplacesPriorSizeForSameMiniMapAndConfig()
    {
        var imagePath = CreateSolidImage(Color.Blue, 30, 30);
        MapOverlayBitmapRenderer.InvalidateImageCache();
        try
        {
            var mini1 = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 30,
                Height: 30,
                Anchors: []);
            using var bitmap1 = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
                100,
                100,
                DisplayTestMatrix.Baseline.Dpi,
                Map: null,
                Status: null,
                ShowStatus: false,
                MiniMap: mini1));

            Assert.Equal(1, MapOverlayBitmapRenderer.MiniMapLayerCacheCount);

            // 模拟滚轮缩放：小地图尺寸发生变化
            var mini2 = new MapOverlayRenderMap(
                imagePath,
                Left: 0,
                Top: 0,
                Width: 35,
                Height: 35,
                Anchors: []);
            using var bitmap2 = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
                100,
                100,
                DisplayTestMatrix.Baseline.Dpi,
                Map: null,
                Status: null,
                ShowStatus: false,
                MiniMap: mini2));

            // 缩放后旧条目必须释放，缓存字典不得膨胀
            Assert.Equal(1, MapOverlayBitmapRenderer.MiniMapLayerCacheCount);
        }
        finally
        {
            MapOverlayBitmapRenderer.InvalidateImageCache();
            File.Delete(imagePath);
        }
    }

    [Fact]
    public void InvalidateImageCache_ClearsAllLayerCaches()
    {
        var imagePath = CreateSolidImage(Color.Green, 30, 30);
        try
        {
            var map = new MapOverlayRenderMap(imagePath, 0, 0, 30, 30, []);
            using var bitmap = MapOverlayBitmapRenderer.Render(new MapOverlayRenderScene(
                50, 50, DisplayTestMatrix.Baseline.Dpi, map, null, false, MiniMap: map));

            Assert.True(MapOverlayBitmapRenderer.MapLayerCacheCount > 0);
            Assert.True(MapOverlayBitmapRenderer.MiniMapLayerCacheCount > 0);

            MapOverlayBitmapRenderer.InvalidateImageCache();

            Assert.Equal(0, MapOverlayBitmapRenderer.MapLayerCacheCount);
            Assert.Equal(0, MapOverlayBitmapRenderer.MiniMapLayerCacheCount);
            Assert.Equal(0, MapOverlayBitmapRenderer.ScaledImageCacheCount);
        }
        finally
        {
            MapOverlayBitmapRenderer.InvalidateImageCache();
            File.Delete(imagePath);
        }
    }
}
