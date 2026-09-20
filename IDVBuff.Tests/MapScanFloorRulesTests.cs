using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapScanFloorRulesTests
{
    [Fact]
    public void FloorOptionsMergeIdsIgnoringCaseAndRequireEveryMap()
    {
        var first = CreateMap("A", "2F", markSecondary: true);
        var second = CreateMap("B", "2f", markSecondary: true);

        var option = Assert.Single(
            MapScanFloorRules.BuildOptions([first, second]),
            candidate => candidate.FloorIdentity == "2f");

        Assert.True(option.IsEligible);
        Assert.Equal("2F", option.DisplayName);
    }

    [Fact]
    public void SecondaryFloorIsUnavailableWhenAnyMapLacksSecondaryGateFeature()
    {
        var first = CreateMap("A", "2f", markSecondary: true);
        var second = CreateMap("B", "2F", markSecondary: false);

        var option = Assert.Single(
            MapScanFloorRules.BuildOptions([first, second]),
            candidate => candidate.FloorIdentity == "2f");

        Assert.False(option.IsEligible);
        Assert.Contains("1 张地图", option.FailureReason);
    }

    [Fact]
    public void ConfiguredFloorResolvesToEachMapsActualCasing()
    {
        var map = CreateMap("A", "2F", markSecondary: true);
        map.ClassProperties.ScanFloorKey = " 2f ";

        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);

        Assert.Equal("2F", floorKey);
        Assert.Equal(
            MapScanFloorRules.SecondaryGateAnchorKey,
            MapScanFloorRules.GetScanFeatureAnchor(map, floorKey)?.Key);
    }

    [Fact]
    public void PrimaryFloorRequiresBothMainAndSideGateMarkers()
    {
        var map = CreateMap("A", "2f", markSecondary: true);
        var primary = map.Recognition.GetFloor("1f")!;
        primary.FindAnchor("side-entrance")!.Bounds = null;

        var option = Assert.Single(
            MapScanFloorRules.BuildOptions([map]),
            candidate => candidate.FloorIdentity == "1f");

        Assert.False(option.IsEligible);
    }

    [Fact]
    public void SecondaryFloorGeometryUsesItsSingleSecondaryGateFeature()
    {
        var map = CreateMap("A", "2f", markSecondary: true);
        map.ClassProperties.ScanFloorKey = "2f";

        var anchors = MapScanFloorRules.GetGeometryAnchors(map, "2f");

        Assert.NotNull(anchors);
        Assert.Equal(
            MapScanFloorRules.SecondaryGateAnchorKey,
            anchors!.Value.Main.Key);
        Assert.Same(anchors.Value.Main, anchors.Value.Side);
    }

    [Fact]
    public void RecognitionFingerprintCanBeBuiltForSecondaryScanFloor()
    {
        var map = CreateMap("A", "2f", markSecondary: true);
        map.ClassProperties.ScanFloorKey = "2f";
        var repository = new MapRepository(Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.SecondaryFingerprint.{Guid.NewGuid():N}"));
        using var service = new MapCvRecognitionService(repository);
        var method = typeof(MapCvRecognitionService).GetMethod(
            "TryCreateFingerprint",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);

        var fingerprint = Assert.IsType<MapGeometryFingerprint>(
            method!.Invoke(service, [map]));

        Assert.Equal("2f", fingerprint.FloorKey);
        Assert.Equal(fingerprint.MainPoint, fingerprint.SidePoint);
        Assert.Equal(200, fingerprint.ReferenceWidth);
        Assert.Equal(150, fingerprint.ReferenceHeight);
    }

    private static MapRecord CreateMap(
        string title,
        string secondaryFloorKey,
        bool markSecondary)
    {
        var map = new MapRecord
        {
            Id = Guid.NewGuid(),
            Title = title,
            Floors =
            [
                new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 },
                new FloorDefinition
                {
                    Key = secondaryFloorKey,
                    DisplayName = secondaryFloorKey,
                    SortOrder = 2
                }
            ]
        };
        map.NormalizeRecognition();
        var primary = map.Recognition.GetFloor("1f")!;
        primary.RecognitionPixelWidth = 200;
        primary.RecognitionPixelHeight = 150;
        primary.FindAnchor("main-entrance")!.Bounds = Bounds(.1d);
        primary.FindAnchor("side-entrance")!.Bounds = Bounds(.3d);
        var secondary = map.Recognition.GetFloor(secondaryFloorKey)!;
        secondary.RecognitionPixelWidth = 200;
        secondary.RecognitionPixelHeight = 150;
        secondary.FindAnchor(MapScanFloorRules.SecondaryGateAnchorKey)!.Bounds =
            markSecondary ? Bounds(.5d) : null;
        return map;
    }

    private static NormalizedRectangle Bounds(double x) => new()
    {
        X = x,
        Y = .2d,
        Width = .1d,
        Height = .1d
    };
}
