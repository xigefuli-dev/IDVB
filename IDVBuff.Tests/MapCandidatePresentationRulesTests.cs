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
            (map, floor) => $"{map.SequenceNumber}-{floor}.png");

        Assert.Equal(new[] { map3.Id, map1.Id, map2.Id },
            result.Select(item => item.Recognition.Map.Id));
        Assert.False(result[0].IsReferenceOnly);
        Assert.False(result[1].IsReferenceOnly);
        Assert.True(result[2].IsReferenceOnly);
        Assert.Equal("未进入本次识别候选", result[2].EvidenceLabel);
    }

    [Fact]
    public void CatalogAppendIncludesMapsOutsideTheCandidateMapClass()
    {
        var candidate = CreateMap(4, "Ranked");
        var sameClass = CreateMap(5, "Ranked");
        var otherClass = CreateMap(6, "Quick");

        var result = MapCandidatePresentationRules.AppendCatalogMaps(
            new[] { CreateChoice(candidate) },
            new[] { otherClass, sameClass, candidate },
            (_, _) => "preview.png");

        Assert.Equal(new[] { candidate.Id, sameClass.Id, otherClass.Id },
            result.Select(item => item.Recognition.Map.Id));
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

    private static MapRecord CreateMap(int sequence, string mapClass)
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = sequence,
            Class = mapClass
        };
        map.NormalizeRecognition();
        return map;
    }

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
