using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;
public sealed partial class MapGeometryRecognitionTests
{

    [Fact]
    public void DegradedSessionAdvanceRetainsOriginalGateScaleEvidence()
    {
        var map = Fingerprint(41, 0.2d, 0.3d, 0.7d, 0.6d).Map;
        map.UpdatedAt = DateTimeOffset.UtcNow;
        map.Recognition.EnsureStandardAnchors();
        var main = map.Recognition.FirstFloor.FindAnchor("main-entrance")!;
        var side = map.Recognition.FirstFloor.FindAnchor("side-entrance")!;
        main.Bounds = new NormalizedRectangle { X = 0.18d, Y = 0.28d, Width = 0.04d, Height = 0.04d };
        side.Bounds = new NormalizedRectangle { X = 0.68d, Y = 0.58d, Width = 0.04d, Height = 0.04d };
        var lockedTransform = new MapOverlayTransform
        {
            ScaleX = 1.4d,
            ScaleY = 1.4d,
            ReferenceWidth = 1000,
            ReferenceHeight = 1000
        };
        var initial = new MapRecognitionResult
        {
            MapId = map.Id,
            Source = MapRecognitionSource.Automatic,
            OverlayTransform = lockedTransform,
            AnchorMatches =
            [
                new CvAnchorEvidence { AnchorId = main.Id, TemplateScale = 0.4d },
                new CvAnchorEvidence { AnchorId = side.Id, TemplateScale = 0.4d }
            ]
        };
        var session = MapAlignmentSession.FromRecognition(map, initial);
        var translated = new MapOverlayTransform
        {
            ScaleX = 1.4d,
            ScaleY = 1.4d,
            OffsetX = 150d,
            OffsetY = 90d,
            ReferenceWidth = 1000,
            ReferenceHeight = 1000
        };

        var advanced = session.Advance(
            map,
            new MapRecognitionResult
            {
                MapId = map.Id,
                Source = MapRecognitionSource.AuxiliaryAnchorTracking,
                OverlayTransform = translated,
                AnchorMatches =
                [
                    new CvAnchorEvidence
                    {
                        AnchorId = Guid.NewGuid(),
                        Score = 0.9d
                    }
                ]
            });

        Assert.Equal(MapAlignmentTrackingMode.AuxiliaryAnchorTracking, advanced.Mode);
        Assert.Equal(0.4d, advanced.GateTemplateScale);
        Assert.Equal(150d, advanced.LockedTransform.OffsetX);
        Assert.Equal(2, advanced.LockedGateEvidence.Count);
    }

}
