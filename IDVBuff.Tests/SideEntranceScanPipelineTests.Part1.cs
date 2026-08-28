using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;
public sealed partial class SideEntranceScanPipelineTests
{

    [Fact]
    public void SideFeatureSeedFallsBackToMarkedSideEntranceCenter()
    {
        var map = CreateMap();
        var profile = MapFloorRules.GetFloorProfile(map, "1f")!;
        profile.SideEntranceFeatureCenterX = 0d;
        profile.SideEntranceFeatureCenterY = 0d;
        profile.SideEntranceFeatureRadius = 0;
        profile.FindAnchor("side-entrance")!.Bounds = new NormalizedRectangle
        {
            X = 0.2d,
            Y = 0.3d,
            Width = 0.1d,
            Height = 0.1d
        };

        var candidate = new SideEntranceScanCandidate
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = 0.91d,
            MatchScale = 1d,
            MatchLocation = new MapScreenRect(100d, 120d, 60d, 60d)
        };

        var created = SideEntranceScanPipeline.TryCreateAlignmentSeed(
            candidate,
            new MapScreenRect(20d, 30d, 600d, 400d),
            out var session,
            out var failureReason);

        Assert.True(created, failureReason);
        Assert.Equal(1d, session.LockedTransform.ScaleX, 8);
        Assert.Equal(-100d, session.LockedTransform.OffsetX, 8);
        Assert.Equal(-100d, session.LockedTransform.OffsetY, 8);
    }

    private static MapRecord CreateMap()
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Floors =
            [
                new FloorDefinition
                {
                    Key = "1f",
                    DisplayName = "1F",
                    SortOrder = 1
                }
            ],
            Recognition = new MapRecognitionProfile
            {
                FirstFloor = new FloorRecognitionProfile
                {
                    FloorKey = "1f",
                    RecognitionPixelWidth = 1000,
                    RecognitionPixelHeight = 800
                }
            }
        };
        map.NormalizeRecognition();
        return map;
    }
}
