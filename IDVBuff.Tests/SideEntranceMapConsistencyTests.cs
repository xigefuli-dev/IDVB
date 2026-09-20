using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class SideEntranceMapConsistencyTests
{
    [Fact]
    public void UserSelectedMapCannotBeReplacedByHigherScoringAlternative()
    {
        var map22 = CreateMap(22);
        var map31 = CreateMap(31);
        var selected = Candidate(map22, score: 0.76d);
        var alternative = Candidate(map31, score: 0.99d);
        var selection = new SideEntranceMapSelection(map22.Id, "1f");

        Assert.True(selection.Matches(selected));
        Assert.False(selection.Matches(alternative));
    }

    [Fact]
    public void AlignmentSeedMustBelongToSelectedMapAndFloor()
    {
        var map22 = CreateMap(22);
        var map31 = CreateMap(31);
        var selection = new SideEntranceMapSelection(map22.Id, "1f");

        Assert.True(selection.Matches(Seed(map22.Id, "1f")));
        Assert.False(selection.Matches(Seed(map31.Id, "1f")));
        Assert.False(selection.Matches(Seed(map22.Id, "2f")));
    }

    [Fact]
    public void AlignmentRecognitionMustKeepMapRecordAndResultIdentityConsistent()
    {
        var map22 = CreateMap(22);
        var map31 = CreateMap(31);
        var selection = new SideEntranceMapSelection(map22.Id, "1f");

        Assert.True(selection.Matches(map22.Id, map22.Id, "1f"));
        Assert.False(selection.Matches(map31.Id, map31.Id, "1f"));
        Assert.False(selection.Matches(map22.Id, map31.Id, "1f"));
        Assert.False(selection.Matches(map22.Id, map22.Id, "2f"));
    }

    [Fact]
    public void CompleteSideEntranceChainAcceptsOnlyOneConsistentMapIdentity()
    {
        var map22 = CreateMap(22);
        var map31 = CreateMap(31);
        var selection = new SideEntranceMapSelection(map22.Id, "1f");
        var selected = Candidate(map22, score: 0.80d);

        Assert.True(selection.Matches(
            selected,
            Seed(map22.Id, "1f"),
            map22.Id,
            map22.Id,
            "1f"));
        Assert.False(selection.Matches(
            selected,
            Seed(map31.Id, "1f"),
            map31.Id,
            map31.Id,
            "1f"));
    }

    private static SideEntranceScanCandidate Candidate(
        MapRecord map,
        double score) =>
        new()
        {
            Map = map,
            FloorKey = "1f",
            MatchScore = score,
            MatchScale = 1d,
            MatchLocation = new MapScreenRect(10d, 20d, 30d, 30d)
        };

    private static MapAlignmentSession Seed(Guid mapId, string floor) =>
        new()
        {
            MapId = mapId,
            FloorKey = floor,
            LockedTransform = new MapOverlayTransform
            {
                ScaleX = 1d,
                ScaleY = 1d,
                ReferenceWidth = 1000,
                ReferenceHeight = 800
            }
        };

    private static MapRecord CreateMap(int sequenceNumber)
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            SequenceNumber = sequenceNumber,
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
