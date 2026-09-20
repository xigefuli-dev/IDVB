using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapCandidatePresentationRulesTests
{
    [Fact]
    public void CandidateOrderIsPreservedAndRemainingMapsFollowSequence()
    {
        var map1 = CreateMap(1, "S1");
        var map2 = CreateMap(2, "S1");
        var map3 = CreateMap(3, "S1");
        var candidates = new[]
        {
            CreateChoice(map3),
            CreateChoice(map1)
        };

        var result = MapCandidatePresentationRules.AppendCatalogMaps(
            candidates,
            new[] { map2, map3, map1 },
            "S1",
            (map, floor) => $"{map.SequenceNumber}-{floor}.png");

        Assert.Equal(new[] { map3.Id, map1.Id, map2.Id },
            result.Select(item => item.Recognition.Map.Id));
        Assert.False(result[0].IsReferenceOnly);
        Assert.False(result[1].IsReferenceOnly);
        Assert.True(result[2].IsReferenceOnly);
        Assert.Equal("未进入本次识别候选", result[2].EvidenceLabel);
    }

    [Fact]
    public void CatalogAppendExcludesMapsOutsideTheCandidateMapClass()
    {
        var candidate = CreateMap(4, "Ranked");
        var sameClass = CreateMap(5, "Ranked");
        var otherClass = CreateMap(6, "Quick");

        var result = MapCandidatePresentationRules.AppendCatalogMaps(
            new[] { CreateChoice(otherClass), CreateChoice(candidate) },
            new[] { otherClass, sameClass, candidate },
            "Ranked",
            (_, _) => "preview.png");

        Assert.Equal(new[] { candidate.Id, sameClass.Id },
            result.Select(item => item.Recognition.Map.Id));
    }

    [Fact]
    public void CatalogAppendUsesTheConfiguredScanFloorForReferencePreview()
    {
        var map = CreateMap(7, "S1");
        map.ClassProperties.ScanFloorKey = "2F";

        var result = MapCandidatePresentationRules.AppendCatalogMaps(
            [],
            [map],
            "S1",
            (_, floor) => $"{floor}.png");

        var choice = Assert.Single(result);
        Assert.Equal("2f", choice.Recognition.Result.Floor);
        Assert.Equal("2f.png", choice.Recognition.FloorImagePath);
    }

    [Fact]
    public void PrimaryFloorSideEntranceCenterUsesTheConfiguredAnchor()
    {
        var map = CreateMap(7, "S1");
        var side = map.Recognition.FirstFloor.FindAnchor("side-entrance")!;
        side.Bounds = new NormalizedRectangle
        {
            X = 0.20d,
            Y = 0.70d,
            Width = 0.10d,
            Height = 0.08d
        };

        var center = MapCandidatePresentationRules.ResolveMapSideEntranceCenter(map);

        Assert.NotNull(center);
        Assert.Equal(0.25d, center.Value.X, 6);
        Assert.Equal(0.74d, center.Value.Y, 6);
    }

    [Fact]
    public void PrimaryFloorPreviewKeepsGatePositionUnlessItLeavesSafeArea()
    {
        var map = CreateMap(7, "S1");
        var side = map.Recognition.FirstFloor.FindAnchor("side-entrance")!;
        side.Bounds = Bounds(0.2d, 0.7d);

        var plan = MapCandidatePresentationRules.ResolveMapPreviewPlan(map, "1f");

        Assert.NotNull(plan);
        Assert.False(plan!.IsSecondaryFloor);
        Assert.Equal(MapCandidatePresentationRules.MapPreviewZoom, plan.Zoom);
        Assert.Equal(plan.Center.X, plan.TargetX, 6);
        Assert.Equal(plan.Center.Y, plan.TargetY, 6);
        Assert.True(MapCandidatePresentationRules.EstimateSourceCoverage(plan) >= 0.80d);
    }

    [Fact]
    public void SecondaryFloorPreviewMagnifiesAndKeepsEdgeGateVisible()
    {
        var map = CreateMap(8, "S1");
        map.ClassProperties.ScanFloorKey = "2f";
        var secondary = map.Recognition.GetFloor("2f")!;
        secondary.FindAnchor(MapScanFloorRules.SecondaryGateAnchorKey)!.Bounds =
            Bounds(0.94d, 0.56d);

        var plan = MapCandidatePresentationRules.ResolveMapPreviewPlan(map, "2f");

        Assert.NotNull(plan);
        Assert.True(plan!.IsSecondaryFloor);
        Assert.Equal(3d, plan.Zoom);
        Assert.Equal(0.90d, plan.TargetX, 6);
        Assert.Equal(plan.Center.Y, plan.TargetY, 6);
        Assert.True(MapCandidatePresentationRules.EstimateSourceCoverage(plan) >= 0.80d);
        Assert.Equal("2F", MapCandidatePresentationRules.ResolveFloorDisplayName(
            map,
            "2f"));
    }

    [Fact]
    public void PreviewAtExtremeCornerStillContainsAtLeastEightyPercentSourcePixels()
    {
        var map = CreateMap(9, "S1");
        map.ClassProperties.ScanFloorKey = "2f";
        var secondary = map.Recognition.GetFloor("2f")!;
        secondary.FindAnchor(MapScanFloorRules.SecondaryGateAnchorKey)!.Bounds =
            Bounds(0d, 0d);

        var plan = MapCandidatePresentationRules.ResolveMapPreviewPlan(map, "2f");

        Assert.NotNull(plan);
        Assert.Equal(0.10d, plan!.TargetX, 6);
        Assert.Equal(0.10d, plan.TargetY, 6);
        Assert.True(MapCandidatePresentationRules.EstimateSourceCoverage(plan) >= 0.80d);
    }

    private static MapRecord CreateMap(int sequence, string mapClass)
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = sequence,
            Class = mapClass
        };
        map.NormalizeRecognition();
        map.Floors =
        [
            new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 },
            new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 }
        ];
        map.NormalizeRecognition();
        return map;
    }

    private static NormalizedRectangle Bounds(double x, double y) => new()
    {
        X = x,
        Y = y,
        Width = 0.02d,
        Height = 0.03d
    };

    private static MapRecognitionChoice CreateChoice(MapRecord map) => new()
    {
        Recognition = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = MapFloorRules.GetPrimaryFloorKey(map),
                Confidence = 0.8d
            }
        }
    };
}
