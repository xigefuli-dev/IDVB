using System.Drawing;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class OverlayNormalizedLayoutTests
{
    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(1f, 0f, 800f, 0f)]
    [InlineData(0f, 1f, 0f, 450f)]
    [InlineData(1f, 1f, 800f, 450f)]
    public void NormalizedPositionUsesTheAvailableTravelRange(
        float x,
        float y,
        float expectedX,
        float expectedY)
    {
        var result = OverlayNormalizedLayout.Resolve(
            new SizeF(1000f, 600f),
            new SizeF(200f, 150f),
            new PointF(x, y),
            null,
            PointF.Empty,
            8f);

        Assert.Equal(expectedX, result.Status!.Value.X, 3);
        Assert.Equal(expectedY, result.Status.Value.Y, 3);
    }

    [Fact]
    public void OversizedPartsAreFittedInsideTheViewport()
    {
        var result = OverlayNormalizedLayout.Resolve(
            new SizeF(800f, 600f),
            null,
            PointF.Empty,
            new SizeF(1600f, 900f),
            new PointF(1f, 1f),
            8f);

        var map = result.MiniMap!.Value;
        Assert.InRange(map.Left, 0f, 800f);
        Assert.InRange(map.Top, 0f, 600f);
        Assert.InRange(map.Right, 0f, 800f);
        Assert.InRange(map.Bottom, 0f, 600f);
        Assert.Equal(map.Width / 1600f, map.Height / 900f, 3);
    }

    [Fact]
    public void EqualOriginKeepsStatusAboveAndPushesMiniMapDown()
    {
        var result = OverlayNormalizedLayout.Resolve(
            new SizeF(1000f, 600f),
            new SizeF(300f, 120f),
            PointF.Empty,
            new SizeF(240f, 180f),
            PointF.Empty,
            8f);

        var status = result.Status!.Value;
        var map = result.MiniMap!.Value;
        Assert.False(status.IntersectsWith(map));
        Assert.Equal(0f, status.Left, 3);
        Assert.Equal(0f, map.Left, 3);
        Assert.True(map.Top >= status.Bottom + 8f);
        Assert.Equal(240f, map.Width, 3);
        Assert.Equal(180f, map.Height, 3);
    }

    [Fact]
    public void CollisionResolutionShrinksMiniMapWhenNoFullSizeRegionExists()
    {
        var result = OverlayNormalizedLayout.Resolve(
            new SizeF(500f, 300f),
            new SizeF(300f, 200f),
            new PointF(0.5f, 0.5f),
            new SizeF(400f, 260f),
            new PointF(0.5f, 0.5f),
            8f);

        var status = result.Status!.Value;
        var map = result.MiniMap!.Value;
        Assert.False(status.IntersectsWith(map));
        Assert.True(map.Width < 400f);
        Assert.InRange(map.Left, 0f, 500f);
        Assert.InRange(map.Top, 0f, 300f);
        Assert.InRange(map.Right, 0f, 500f);
        Assert.InRange(map.Bottom, 0f, 300f);
    }

    [Fact]
    public void BottomMiniMapGrowthPushesStatusUpAndPreservesBothXPositions()
    {
        var viewport = new SizeF(1000f, 600f);
        var statusPosition = new PointF(0.35f, 0.70f);
        var miniMapPosition = new PointF(0f, 1f);

        var initial = OverlayNormalizedLayout.Resolve(
            viewport,
            new SizeF(300f, 100f),
            statusPosition,
            new SizeF(240f, 180f),
            miniMapPosition,
            8f);
        var grown = OverlayNormalizedLayout.Resolve(
            viewport,
            new SizeF(300f, 100f),
            statusPosition,
            new SizeF(360f, 260f),
            miniMapPosition,
            8f);

        var initialStatus = initial.Status!.Value;
        var initialMap = initial.MiniMap!.Value;
        var grownStatus = grown.Status!.Value;
        var grownMap = grown.MiniMap!.Value;
        Assert.Equal(initialStatus.X, grownStatus.X, 3);
        Assert.Equal(0f, initialMap.X, 3);
        Assert.Equal(0f, grownMap.X, 3);
        Assert.Equal(600f, initialMap.Bottom, 3);
        Assert.Equal(600f, grownMap.Bottom, 3);
        Assert.Equal(grownMap.Top - 8f, grownStatus.Bottom, 3);
        Assert.True(grownStatus.Top < initialStatus.Top);
    }

    [Fact]
    public void TopBoundaryTurnsRemainingPressureBackOntoLowerPart()
    {
        var result = OverlayNormalizedLayout.Resolve(
            new SizeF(800f, 500f),
            new SizeF(260f, 100f),
            PointF.Empty,
            new SizeF(300f, 180f),
            new PointF(0.2f, 0.05f),
            8f);

        var status = result.Status!.Value;
        var map = result.MiniMap!.Value;
        Assert.Equal(0f, status.Top, 3);
        Assert.Equal(status.Bottom + 8f, map.Top, 3);
        Assert.Equal(0f, status.Left, 3);
        Assert.Equal(100f, map.Left, 3);
    }

    [Fact]
    public void LegacyPixelOffsetsMigrateAndAllRatiosAreClamped()
    {
        var settings = MapRuntimeSettings.CreateDefault();
        settings.SchemaVersion = 13;
        settings.StatusOffsetX = -200d;
        settings.StatusOffsetY = 250d;
        settings.MiniMapOffsetX = 750d;
        settings.MiniMapOffsetY = 50d;
        settings.StatusScale = 2d;
        settings.MiniMapScale = -1d;

        settings.Normalize();

        Assert.Equal(MapRuntimeSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(0d, settings.StatusOffsetX);
        Assert.Equal(0.5d, settings.StatusOffsetY);
        Assert.Equal(1d, settings.MiniMapOffsetX);
        Assert.Equal(0.1d, settings.MiniMapOffsetY);
        Assert.Equal(1d, settings.StatusScale);
        Assert.Equal(0d, settings.MiniMapScale);
    }

    [Fact]
    public void ContinuousScaleSweepIsNumericallyStableAndAlwaysConstrained()
    {
        var viewport = new SizeF(379f, 211f);
        for (var step = 0; step <= 1000; step++)
        {
            var scale = step / 1000f;
            var statusSize = new SizeF(101.73333f * scale, 30.499998f * scale);
            foreach (var position in new[] { 0f, 0.05f, 0.5f, 0.95f, 1f })
            {
                var result = OverlayNormalizedLayout.Resolve(
                    viewport,
                    statusSize.Width > 0f ? statusSize : null,
                    new PointF(position, 1f - position),
                    new SizeF(287.00003f, 203.00002f),
                    new PointF(0f, 1f),
                    3f);

                AssertInside(viewport, result.Status);
                AssertInside(viewport, result.MiniMap);
                if (result.Status is { } status && result.MiniMap is { } miniMap)
                    Assert.False(status.IntersectsWith(miniMap));
            }
        }
    }

    private static void AssertInside(SizeF viewport, RectangleF? rectangle)
    {
        if (rectangle is not { IsEmpty: false } value)
            return;
        const float tolerance = 0.001f;
        Assert.True(value.Left >= -tolerance);
        Assert.True(value.Top >= -tolerance);
        Assert.True(value.Right <= viewport.Width + tolerance);
        Assert.True(value.Bottom <= viewport.Height + tolerance);
    }
}
