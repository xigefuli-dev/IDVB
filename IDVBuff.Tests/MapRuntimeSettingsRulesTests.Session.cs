using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed partial class MapRuntimeSettingsRulesTests
{
    [Fact]
    public void SelectedMapIdentityIsPersistedByCloneWithoutAlignmentState()
    {
        var selectedMapId = Guid.NewGuid();
        var settings = new MapRuntimeSettings
        {
            SelectedMapId = selectedMapId
        };

        settings.Normalize();
        var clone = settings.Clone();

        Assert.Equal(selectedMapId, clone.SelectedMapId);
        Assert.Null(new MapRuntimeSettings().SelectedMapId);
    }

    [Fact]
    public void EmptySelectedMapIdentityNormalizesToNoSelection()
    {
        var settings = new MapRuntimeSettings
        {
            SelectedMapId = Guid.Empty
        };

        settings.Normalize();

        Assert.Null(settings.SelectedMapId);
    }

    [Fact]
    public void SelectedMapIdentityRoundTripsThroughPersistedJson()
    {
        var selectedMapId = Guid.NewGuid();
        var json = JsonSerializer.Serialize(new MapRuntimeSettings
        {
            SelectedMapId = selectedMapId
        });

        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json);
        restored!.Normalize();

        Assert.Equal(selectedMapId, restored.SelectedMapId);
    }

    [Fact]
    public void FloorDisplayCalibrationClonesAndRoundTripsIndependently()
    {
        var settings = new MapRuntimeSettings
        {
            MapViewportRegion = new NormalizedRectangle
            {
                X = 0.1,
                Y = 0.1,
                Width = 0.8,
                Height = 0.8
            },
            CalibrationClientWidth = 1706,
            CalibrationClientHeight = 1066,
            CalibrationVersion = MapRuntimeSettings.CurrentCalibrationVersion,
            FloorDisplayRegion = new NormalizedRectangle
            {
                X = 0.7,
                Y = 0.05,
                Width = 0.2,
                Height = 0.1
            },
            FloorCalibrationClientWidth = 1706,
            FloorCalibrationClientHeight = 1066,
            FloorCalibrationVersion = MapRuntimeSettings.CurrentCalibrationVersion
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();
        var clone = restored.Clone();
        clone.FloorDisplayRegion!.X = 0.2;

        Assert.True(restored.IsMapViewportCalibrated);
        Assert.True(restored.IsFloorDisplayCalibrated);
        Assert.Equal(0.7, restored.FloorDisplayRegion!.X);
        Assert.Equal(0.2, clone.FloorDisplayRegion.X);
    }

    [Fact]
    public void LegacySettingsKeepMapCalibrationButRequireFloorCalibration()
    {
        var settings = new MapRuntimeSettings
        {
            MapViewportRegion = new NormalizedRectangle
            {
                X = 0.1,
                Y = 0.1,
                Width = 0.8,
                Height = 0.8
            },
            CalibrationClientWidth = 1706,
            CalibrationClientHeight = 1066,
            CalibrationVersion = MapRuntimeSettings.CurrentCalibrationVersion
        };

        settings.Normalize();

        Assert.True(settings.IsMapViewportCalibrated);
        Assert.False(settings.IsFloorDisplayCalibrated);
        Assert.Null(settings.FloorDisplayRegion);
    }

    [Theory]
    [InlineData("1f", MapFloorRoute.FirstFloorAlignment)]
    [InlineData("2f", MapFloorRoute.SecondFloorAlignment)]
    public void SuccessfulFloorResultsRouteToOnlyTheirOwnStrategy(
        string floor,
        MapFloorRoute expectedRoute)
    {
        var result = new MapFloorRecognitionResult
        {
            Succeeded = true,
            Floor = floor,
            EndToEndMilliseconds = 25d
        };

        Assert.Equal(expectedRoute, MapFloorRecognitionRules.GetRoute(result));
    }

    [Fact]
    public void EveryScanAndAlignmentIntentRequiresAConfirmedFloor()
    {
        Assert.True(MapFloorRecognitionRules.RequiresConfirmedFirstFloor(
            MapFloorRecognitionIntent.AutomaticMapOpen));
        Assert.True(MapFloorRecognitionRules.RequiresConfirmedFirstFloor(
            MapFloorRecognitionIntent.QuickScan));
        Assert.True(MapFloorRecognitionRules.RequiresConfirmedFirstFloor(
            MapFloorRecognitionIntent.ManualRecognition));
        Assert.True(
            MapFloorRecognitionRules.GetOperationPriority(
                MapFloorRecognitionIntent.ManualRecognition)
            > MapFloorRecognitionRules.GetOperationPriority(
                MapFloorRecognitionIntent.QuickScan));
    }

    [Fact]
    public void ManualFloorOverridesDisplayedFloorAndCannotAutomaticallyFallback()
    {
        var main = new FloorRecognitionProfile { FloorKey = "main" };
        var upper = new FloorRecognitionProfile { FloorKey = "upper" };
        var map = new MapRecord
        {
            Floors =
            [
                new FloorDefinition { Key = "main", SortOrder = 1 },
                new FloorDefinition { Key = "upper", SortOrder = 2 }
            ],
            Recognition = new MapRecognitionProfile
            {
                FirstFloor = main,
                SecondFloor = upper,
                Floors = new Dictionary<string, FloorRecognitionProfile>
                {
                    ["main"] = main,
                    ["upper"] = upper
                }
            }
        };

        var manual = Assert.IsType<MapAlignmentFloorSelection>(
            MapFloorRecognitionRules.ResolvePreferredAlignmentFloor(
                map,
                manualFloorKey: "upper",
                displayedFloorKey: "main"));
        Assert.Equal("upper", manual.FloorKey);
        Assert.Equal(MapAlignmentFloorSource.ManualOverride, manual.Source);
        Assert.False(MapFloorRecognitionRules.MayFallbackToAutomaticFloor(
            manual.Source));

        var displayed = Assert.IsType<MapAlignmentFloorSelection>(
            MapFloorRecognitionRules.ResolvePreferredAlignmentFloor(
                map,
                manualFloorKey: "missing",
                displayedFloorKey: "main"));
        Assert.Equal(MapAlignmentFloorSource.DisplayedMiniMap, displayed.Source);
        Assert.True(MapFloorRecognitionRules.MayFallbackToAutomaticFloor(
            displayed.Source));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidFloorSuccessIsAlwaysRejected(double elapsed)
    {
        var result = new MapFloorRecognitionResult
        {
            Succeeded = true,
            Floor = "1f",
            EndToEndMilliseconds = elapsed
        };

        Assert.False(MapFloorRecognitionRules.IsPublishableSuccess(result));
        Assert.Equal(MapFloorRoute.Reject, MapFloorRecognitionRules.GetRoute(result));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(100d)]
    public void FloorSuccessAtOrInsidePerformanceBudgetIsFast(double elapsed)
    {
        var result = new MapFloorRecognitionResult
        {
            Succeeded = true,
            Floor = "1f",
            EndToEndMilliseconds = elapsed
        };

        Assert.True(MapFloorRecognitionRules.IsPublishableSuccess(result));
        Assert.True(MapFloorRecognitionRules.IsWithinPerformanceBudget(result));
        Assert.Equal(
            MapFloorRoute.FirstFloorAlignment,
            MapFloorRecognitionRules.GetRoute(result));
    }

    [Fact]
    public void SlowValidFloorResultStillRoutesButIsMarkedOverBudget()
    {
        var result = new MapFloorRecognitionResult
        {
            Succeeded = true,
            Floor = "1f",
            EndToEndMilliseconds = 350d
        };

        Assert.True(MapFloorRecognitionRules.IsPublishableSuccess(result));
        Assert.False(MapFloorRecognitionRules.IsWithinPerformanceBudget(result));
        Assert.Equal(
            MapFloorRoute.FirstFloorAlignment,
            MapFloorRecognitionRules.GetRoute(result));
    }

    [Fact]
    public void FirstFloorRequiresTwoConsecutiveFrames()
    {
        var tracker = new MapFloorStabilityTracker();

        Assert.False(tracker.Observe("1f"));
        Assert.True(tracker.Observe("1f"));
    }

    [Fact]
    public void SecondFloorRequiresThreeConsecutiveFrames()
    {
        var tracker = new MapFloorStabilityTracker();

        Assert.False(tracker.Observe("2f"));
        Assert.False(tracker.Observe("2f"));
        Assert.True(tracker.Observe("2f"));
    }

    [Fact]
    public void OppositeOrInvalidFrameResetsFloorConfirmation()
    {
        var tracker = new MapFloorStabilityTracker();

        Assert.False(tracker.Observe("2f"));
        Assert.False(tracker.Observe("2f"));
        Assert.False(tracker.Observe("1f"));
        tracker.Reset();
        Assert.False(tracker.Observe("2f"));
        Assert.False(tracker.Observe("2f"));
        Assert.True(tracker.Observe("2f"));
    }

    [Fact]
    public void RepeatedCaptureOfSameFrameDoesNotCountAsConfirmation()
    {
        var tracker = new MapFloorStabilityTracker();

        Assert.False(tracker.Observe("2f", 100, 16));
        Assert.False(tracker.Observe("2f", 100, 16));
        Assert.False(tracker.Observe("2f", 116, 16));
        Assert.False(tracker.Observe("2f", 131, 16));
        Assert.True(tracker.Observe("2f", 132, 16));
    }

    [Fact]
    public void SameWindowSignatureCalibrationsForBothFloorsSurviveNormalization()
    {
        var signature = DisplayTestMatrix.Baseline.CreateSignature();
        var mapId = Guid.NewGuid();
        var mapUpdatedAt = DateTimeOffset.UtcNow;
        MapAlignmentCalibration Calibration(string floor) => new()
        {
            MapId = mapId,
            Floor = floor,
            MapUpdatedAt = mapUpdatedAt,
            ReferenceWidth = 1600,
            ReferenceHeight = 1200,
            UniformScale = 1.25d,
            RotationDegrees = 0d,
            ClientWidth = signature.ClientWidth,
            ClientHeight = signature.ClientHeight,
            ViewportWidth = signature.ViewportWidth,
            ViewportHeight = signature.ViewportHeight,
            Dpi = signature.Dpi,
            Confidence = 0.91d,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var settings = new MapRuntimeSettings
        {
            AlignmentCalibrations =
            [
                Calibration("1f"),
                Calibration("2f")
            ]
        };

        settings.Normalize();

        Assert.Equal(2, settings.AlignmentCalibrations.Count);
        Assert.Contains(
            settings.AlignmentCalibrations,
            calibration => calibration.Floor == "1f");
        Assert.Contains(
            settings.AlignmentCalibrations,
            calibration => calibration.Floor == "2f");
    }

    [Fact]
    public void ResearchAndPerFloorScaleSettingsCloneAndNormalize()
    {
        var mapId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var floorCalibration = new MapFloorScaleCalibration
        {
            MapId = mapId,
            MapUpdatedAt = updatedAt,
            PrimaryFloorKey = "main",
            FloorKey = "upper"
        };
        Assert.True(floorCalibration.TryAddTrustedSample(
            1.12d,
            0.9d,
            updatedAt,
            out _));
        var settings = new MapRuntimeSettings
        {
            CollectAlignmentResearchData = true,
            FloorScaleCalibrations = [floorCalibration]
        };

        var clone = settings.Clone();
        clone.Normalize();

        Assert.True(clone.CollectAlignmentResearchData);
        var persisted = Assert.Single(clone.FloorScaleCalibrations);
        Assert.NotSame(floorCalibration, persisted);
        Assert.Equal("upper", persisted.FloorKey);
        Assert.Equal(1.12d, persisted.MedianRatio, 8);
        Assert.Equal(MapRuntimeSettings.CurrentSchemaVersion, clone.SchemaVersion);
    }
}
