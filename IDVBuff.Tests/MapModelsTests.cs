using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapModelsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacyProfileMigratesSecondFloorAnchorToOptional(int schemaVersion)
    {
        var profile = new MapRecognitionProfile
        {
            SchemaVersion = schemaVersion,
            FirstFloor = new FloorRecognitionProfile
            {
                Floor = MapFloor.First,
                RecognitionRegion = new NormalizedRectangle { Width = 1d, Height = 1d },
                Anchors =
                [
                    Anchor("main-entrance", RecognitionAnchorRole.Optional),
                    Anchor("side-entrance", RecognitionAnchorRole.Optional)
                ]
            },
            SecondFloor = new FloorRecognitionProfile
            {
                Floor = MapFloor.Second,
                Anchors = [Anchor("second-floor-primary", RecognitionAnchorRole.Required, marked: false)]
            }
        };

        profile.EnsureStandardAnchors();

        Assert.Equal(8, profile.SchemaVersion);
        Assert.Equal(RecognitionAnchorRole.Required, profile.FirstFloor.FindAnchor("main-entrance")!.Role);
        Assert.Equal(RecognitionAnchorRole.Required, profile.FirstFloor.FindAnchor("side-entrance")!.Role);
        Assert.Equal(RecognitionAnchorRole.Optional, profile.SecondFloor.FindAnchor("second-floor-primary")!.Role);
        Assert.True(profile.HasRequiredIdentificationData());
    }

    [Fact]
    public void MissingExplicitFirstFloorRegionUsesFullImageAndIsRecognitionReady()
    {
        var profile = new MapRecognitionProfile();
        profile.EnsureStandardAnchors();
        profile.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.05d, Height = 0.05d };
        profile.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.7d, Y = 0.7d, Width = 0.05d, Height = 0.05d };

        Assert.True(profile.HasRequiredIdentificationData());
    }

    [Fact]
    public void RecognitionProfileConverterUsesFloorKeysInsteadOfJsonPropertyOrder()
    {
        const string json = """
            {
              "SchemaVersion": 6,
              "Floors": {
                "2f": { "Floor": 2, "FloorKey": "2f" },
                "1f": { "Floor": 1, "FloorKey": "1f" }
              }
            }
            """;

        var profile = JsonSerializer.Deserialize<MapRecognitionProfile>(json)!;

        Assert.Equal("1f", profile.FirstFloor.FloorKey);
        Assert.Equal("2f", profile.SecondFloor.FloorKey);
    }

    [Fact]
    public void SingleCanonicalFloorDoesNotShareSecondFloorCompatibilityState()
    {
        var first = new FloorRecognitionProfile
        {
            Floor = MapFloor.Second,
            FloorKey = "1f",
            Annotations =
            [
                new MapAnnotation { Type = MapAnnotationType.Outline }
            ]
        };
        var profile = new MapRecognitionProfile
        {
            Floors = new Dictionary<string, FloorRecognitionProfile>
            {
                ["1f"] = first
            }
        };

        profile.NormalizeForFloors(
        [
            new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 }
        ]);

        Assert.Same(first, profile.Floors["1f"]);
        Assert.Same(profile.Floors["1f"], profile.FirstFloor);
        Assert.NotSame(profile.FirstFloor, profile.SecondFloor);
        Assert.DoesNotContain("2f", profile.Floors.Keys);
        Assert.Single(profile.FirstFloor.Annotations);
        Assert.Empty(profile.SecondFloor.Annotations);

        profile.FirstFloor.Annotations.Clear();
        Assert.Empty(profile.FirstFloor.Annotations);
        Assert.Empty(profile.SecondFloor.Annotations);
    }

    [Fact]
    public void CloneUsesCanonicalProfilesWithoutCrossFloorSharing()
    {
        var profile = new MapRecognitionProfile();
        profile.EnsureStandardAnchors();
        profile.FirstFloor.Annotations.Add(new MapAnnotation { Type = MapAnnotationType.Outline });

        var clone = profile.Clone();

        Assert.Same(clone.Floors["1f"], clone.FirstFloor);
        Assert.Same(clone.Floors["2f"], clone.SecondFloor);
        Assert.NotSame(clone.Floors["1f"], clone.Floors["2f"]);
        Assert.NotSame(profile.Floors["1f"], clone.Floors["1f"]);
        Assert.Single(clone.FirstFloor.Annotations);

        clone.FirstFloor.Annotations.Clear();
        Assert.Single(profile.FirstFloor.Annotations);
        Assert.Empty(clone.SecondFloor.Annotations);
    }

    [Fact]
    public void ConverterRepairsSingleFloorWithWrongLegacyEnumWithoutAddingSecondFloor()
    {
        const string json = """
            {
              "SchemaVersion": 7,
              "Floors": {
                "1f": { "Floor": 2, "FloorKey": "1f" }
              }
            }
            """;

        var profile = JsonSerializer.Deserialize<MapRecognitionProfile>(json)!;

        Assert.Equal(MapFloor.First, profile.Floors["1f"].Floor);
        Assert.Equal("1f", profile.FirstFloor.FloorKey);
        Assert.NotSame(profile.FirstFloor, profile.SecondFloor);
        Assert.Single(profile.Floors);
    }

    [Fact]
    public void CanonicalSingleFloorIgnoresLegacySecondFloorPayload()
    {
        const string json = """
            {
              "SchemaVersion": 7,
              "SecondFloor": {
                "Floor": 2,
                "FloorKey": "2f",
                "Annotations": [{ "Type": 1 }]
              },
              "Floors": {
                "1f": { "Floor": 1, "FloorKey": "1f" }
              }
            }
            """;

        var profile = JsonSerializer.Deserialize<MapRecognitionProfile>(json)!;

        Assert.Single(profile.Floors);
        Assert.Empty(profile.SecondFloor.Annotations);
        Assert.NotSame(profile.FirstFloor, profile.SecondFloor);
    }

    [Fact]
    public void RecordNormalizationCreatesIndependentProfileForMissingSecondFloor()
    {
        var first = new FloorRecognitionProfile
        {
            Floor = MapFloor.First,
            FloorKey = "1f"
        };
        var record = new MapRecord
        {
            Floors =
            [
                new FloorDefinition { Key = "1f", DisplayName = "1F", SortOrder = 1 },
                new FloorDefinition { Key = "2f", DisplayName = "2F", SortOrder = 2 }
            ],
            Recognition = new MapRecognitionProfile
            {
                Floors = new Dictionary<string, FloorRecognitionProfile>
                {
                    ["1f"] = first
                },
                FirstFloor = first,
                SecondFloor = first
            }
        };

        record.NormalizeRecognition();

        Assert.Equal(["1f", "2f"], record.Recognition.Floors.Keys.OrderBy(key => key));
        Assert.NotSame(record.Recognition.Floors["1f"], record.Recognition.Floors["2f"]);
        Assert.Same(record.Recognition.Floors["1f"], record.Recognition.FirstFloor);
        Assert.Same(record.Recognition.Floors["2f"], record.Recognition.SecondFloor);
    }

    [Fact]
    public void FirstFloorGateMarkersAreEnoughForEditorConfirmation()
    {
        var profile = new MapRecognitionProfile();
        profile.EnsureStandardAnchors();
        profile.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.05d, Height = 0.05d };
        profile.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            new NormalizedRectangle { X = 0.7d, Y = 0.7d, Width = 0.05d, Height = 0.05d };

        Assert.True(profile.HasFirstFloorGateMarkers());
    }

    [Fact]
    public void EnsureStandardAnchorsPreservesCustomFloorKeys()
    {
        var profile = new MapRecognitionProfile();
        profile.EnsureStandardAnchors();
        profile.FirstFloor.FloorKey = "ground";
        profile.SecondFloor.FloorKey = "roof";
        profile.Floors.Clear();

        profile.EnsureStandardAnchors();

        Assert.Equal("ground", profile.FirstFloor.FloorKey);
        Assert.Equal("roof", profile.SecondFloor.FloorKey);
        Assert.Same(profile.FirstFloor, profile.Floors["ground"]);
        Assert.Same(profile.SecondFloor, profile.Floors["roof"]);
        Assert.DoesNotContain("1f", profile.Floors.Keys);
        Assert.DoesNotContain("2f", profile.Floors.Keys);
    }

    [Fact]
    public void ChangingRegionPreservesContainedAnchorSourcePositionAndClearsOutOfBoundsAnchor()
    {
        var profile = new FloorRecognitionProfile
        {
            Floor = MapFloor.First,
            RecognitionRegion = new NormalizedRectangle { Width = 1d, Height = 1d },
            Anchors =
            [
                new RecognitionAnchor
                {
                    Key = "inside",
                    Bounds = new NormalizedRectangle { X = 0.2d, Y = 0.3d, Width = 0.1d, Height = 0.1d }
                },
                new RecognitionAnchor
                {
                    Key = "outside",
                    Bounds = new NormalizedRectangle { X = 0.92d, Y = 0.3d, Width = 0.05d, Height = 0.05d }
                }
            ]
        };
        var originalSourceBounds = MapRecognitionCoordinates.ToSourceRectangle(
            profile.FindAnchor("inside")!.Bounds!,
            profile.GetEffectiveRecognitionRegion());
        var newRegion = new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.8d, Height = 0.8d };

        MapRecognitionCoordinates.ApplyRecognitionRegion(profile, newRegion);

        var convertedSourceBounds = MapRecognitionCoordinates.ToSourceRectangle(
            profile.FindAnchor("inside")!.Bounds!,
            profile.GetEffectiveRecognitionRegion());
        Assert.Equal(originalSourceBounds.X, convertedSourceBounds.X, 8);
        Assert.Equal(originalSourceBounds.Y, convertedSourceBounds.Y, 8);
        Assert.Equal(originalSourceBounds.Width, convertedSourceBounds.Width, 8);
        Assert.Equal(originalSourceBounds.Height, convertedSourceBounds.Height, 8);
        Assert.Null(profile.FindAnchor("outside")!.Bounds);
    }

    [Fact]
    public void SchemaSixPaletteAnnotationMigratesToCanonicalRgbOnEveryFloor()
    {
        var third = new FloorRecognitionProfile
        {
            FloorKey = "roof",
            Annotations =
            [
                new MapAnnotation
                {
                    Type = MapAnnotationType.Outline,
                    ColorIndex = 6,
                    Bounds = new NormalizedRectangle { Width = 0.2d, Height = 0.2d }
                }
            ]
        };
        var profile = new MapRecognitionProfile
        {
            SchemaVersion = 6,
            Floors = new Dictionary<string, FloorRecognitionProfile> { ["roof"] = third }
        };

        profile.EnsureStandardAnchors();

        Assert.Equal(8, profile.SchemaVersion);
        Assert.Equal("#AF52DE", third.Annotations[0].ColorHex);
        Assert.True(third.Annotations[0].IsValid);
    }

    [Fact]
    public void DirectedLineRequiresDistinctNormalizedEndpointsAndPreservesRgb()
    {
        var line = new MapAnnotation
        {
            Type = MapAnnotationType.Line,
            ColorHex = "#12abef",
            Start = new NormalizedPoint { X = 0.8d, Y = 0.2d },
            End = new NormalizedPoint { X = 0.1d, Y = 0.9d }
        };

        Assert.True(line.IsValid);
        Assert.Equal("#12ABEF", line.EffectiveColorHex);
        var clone = line.Clone();
        Assert.Equal(0.8d, clone.Start!.X);
        Assert.Equal(0.1d, clone.End!.X);

        line.End = new NormalizedPoint { X = 0.8d, Y = 0.2d };
        Assert.False(line.IsValid);
    }

    private static RecognitionAnchor Anchor(
        string key,
        RecognitionAnchorRole role,
        bool marked = true) =>
        new()
        {
            Key = key,
            Role = role,
            Bounds = marked
                ? new NormalizedRectangle { X = 0.1d, Y = 0.1d, Width = 0.05d, Height = 0.05d }
                : null
        };
}
