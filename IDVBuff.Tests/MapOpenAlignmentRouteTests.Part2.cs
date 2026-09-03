using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class MapOpenAlignmentRouteTests
{
    [Fact]
    public void ReopenDriftCannotComparePrimaryAndLowStructureFloors()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var primary = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "1f",
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 0.736d,
                    ScaleY = 0.736d,
                    ReferenceWidth = 1000,
                    ReferenceHeight = 800,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            }
        };
        var basement = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "b1f",
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 0.4671d,
                    ScaleY = 0.4671d,
                    ReferenceWidth = 700,
                    ReferenceHeight = 600,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            }
        };

        Assert.False(MapOpenAlignmentRouteRules.CanCompareMapOpenDrift(
            primary,
            basement,
            "b1f"));
        Assert.True(MapOpenAlignmentRouteRules.CanCompareMapOpenDrift(
            basement,
            basement,
            "b1f"));
    }

    [Fact]
    public void OrdinaryTransformlessRecognitionRemainsRejected()
    {
        var map = new MapRecord { Id = Guid.NewGuid() };
        map.Recognition.EnsureStandardAnchors();

        Assert.Throws<InvalidOperationException>(() =>
            MapOpenAlignmentRouteRules.ResolveMapOpenAlignmentSession(
                map,
                new MapRecognitionResult { MapId = map.Id, Floor = "1f" },
                pendingSideEntranceSeed: null,
                previous: null,
                canReusePrevious: false));
    }

    [Fact]
    public void AlignmentContextNormalizesFloorButKeepsCaptureGeometryIndependent()
    {
        var context = new MapAlignmentContextKey(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            " 2F ",
            2560,
            1600,
            1322,
            1053,
            " edges-v3 ").Normalize();

        Assert.Equal("2f", context.FloorKey);
        Assert.Equal("edges-v3", context.StructureGeneration);
        Assert.NotEqual(
            context,
            context with { ViewportWidth = context.ViewportWidth + 1 });
    }
}
