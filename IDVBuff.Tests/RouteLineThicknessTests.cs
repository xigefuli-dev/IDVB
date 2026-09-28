using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class RouteLineThicknessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderingChangesBothLayersAndRestoresThinFromCache(bool miniMap)
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"idvb-route-{Guid.NewGuid():N}.png");
        using (var source = new Bitmap(80, 80, PixelFormat.Format32bppPArgb))
            source.Save(imagePath, ImageFormat.Png);
        try
        {
            var map = new MapOverlayRenderMap(imagePath, 0, 0, 80, 80, [], Annotations:
            [
                new(MapAnnotationType.Line, 0, "#FF0000", null,
                    new() { X = 0.2, Y = 0.5 }, new() { X = 0.8, Y = 0.5 })
            ]);
            var counts = new List<int>();
            foreach (var level in new[] { 0, 1, 2, 3, 0 })
            {
                var configuredMap = map with { RouteLineThickness = level };
                using var bitmap = miniMap
                    ? MapOverlayBitmapRenderer.Render(new(160, 160, 96, null, null, false,
                        MiniMap: configuredMap, MiniMapOpacity: 1))
                    : MapOverlayBitmapRenderer.RenderMapLayer(configuredMap, 96,
                        false, false, false, false, true, 1);
                var count = 0;
                for (var x = 0; x < bitmap.Width; x++)
                    for (var y = 0; y < bitmap.Height; y++)
                        count += bitmap.GetPixel(x, y).A;
                counts.Add(count);
            }
            Assert.True(counts[0] > 0);
            Assert.True(counts[0] < counts[1] && counts[1] < counts[2] && counts[2] < counts[3],
                string.Join(", ", counts));
            Assert.Equal(counts[0], counts[4]);
        }
        finally
        {
            MapOverlayBitmapRenderer.InvalidateImageCache();
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task LegacySettingsDefaultToMediumAndAllFourLevelsPersist()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"idvb-route-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{}");
            var repository = new MapRuntimeSettingsRepository(directory);
            var settings = await repository.LoadAsync();
            Assert.Equal(1, settings.RouteLineThickness);
            foreach (var level in Enumerable.Range(0, 4))
            {
                settings.RouteLineThickness = level;
                Assert.Equal(level, settings.Clone().RouteLineThickness);
                await repository.SaveAsync(settings);
                Assert.Equal(level, (await repository.LoadAsync()).RouteLineThickness);
            }
            settings.RouteLineThickness = 99;
            settings.Normalize();
            Assert.Equal(3, settings.RouteLineThickness);
            settings.RouteLineThickness = -1;
            settings.Normalize();
            Assert.Equal(0, settings.RouteLineThickness);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
