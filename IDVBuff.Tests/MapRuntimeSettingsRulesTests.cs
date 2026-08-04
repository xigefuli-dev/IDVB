using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed class MapRuntimeSettingsRulesTests
{
    [Fact]
    public void MissingAlignmentModeDefaultsToIndependentAxes()
    {
        Assert.Equal(
            MapOverlayAlignmentMode.IndependentAxes,
            default(MapOverlayAlignmentMode));
    }

    [Fact]
    public void InvalidAlignmentModeNormalizesToIndependentAxes()
    {
        Assert.Equal(
            MapOverlayAlignmentMode.IndependentAxes,
            MapRuntimeSettingsRules.NormalizeAlignmentMode((MapOverlayAlignmentMode)999));
    }

    [Fact]
    public void LegacyCalibrationRequiresOneTimeRecalibration()
    {
        var display = DisplayTestMatrix.Baseline;
        Assert.False(MapRuntimeSettingsRules.IsCalibrationCurrent(
            regionIsValid: true,
            clientWidth: display.PixelWidth,
            clientHeight: display.PixelHeight,
            calibrationVersion: 0));
        Assert.True(MapRuntimeSettingsRules.IsCalibrationCurrent(
            regionIsValid: true,
            clientWidth: display.PixelWidth,
            clientHeight: display.PixelHeight,
            calibrationVersion: MapRuntimeSettingsRules.CurrentCalibrationVersion));
    }

    [Fact]
    public void RecognitionTuningNormalizesInvalidAndOutOfRangeValues()
    {
        var tuning = new MapRecognitionTuning
        {
            GateTemplateThreshold = double.NaN,
            MinimumConfidence = 2d,
            VectorErrorTolerance = -1d,
            AmbiguityMargin = double.PositiveInfinity,
            ConfirmationAdvantage = 0d
        };

        tuning.Normalize();

        Assert.Equal(
            MapRecognitionTuning.DefaultGateTemplateThreshold,
            tuning.GateTemplateThreshold);
        Assert.Equal(0.95d, tuning.MinimumConfidence);
        Assert.Equal(0.01d, tuning.VectorErrorTolerance);
        Assert.Equal(
            MapRecognitionTuning.DefaultAmbiguityMargin,
            tuning.AmbiguityMargin);
        Assert.Equal(0.01d, tuning.ConfirmationAdvantage);
    }

    [Fact]
    public void SessionTuningAllowsZeroOpeningAnimationDelay()
    {
        var tuning = new MapSessionTuning
        {
            OpeningAnimationDelayMilliseconds = 0
        };

        tuning.Normalize();

        Assert.Equal(0, tuning.OpeningAnimationDelayMilliseconds);
    }

    [Fact]
    public void RecognitionTuningCloneIsIndependent()
    {
        var original = new MapRecognitionTuning
        {
            ForceBestRecognitionResult = false
        };
        var clone = original.Clone();

        clone.MinimumConfidence = 0.8d;
        clone.ForceBestRecognitionResult = true;

        Assert.Equal(
            MapRecognitionTuning.DefaultMinimumConfidence,
            original.MinimumConfidence);
        Assert.Equal(0.8d, clone.MinimumConfidence);
        Assert.False(original.ForceBestRecognitionResult);
        Assert.True(clone.ForceBestRecognitionResult);
    }

    [Fact]
    public void FloorRecognitionTuningNormalizesAndClones()
    {
        var tuning = new MapFloorRecognitionTuning
        {
            MinimumConfidence = double.NaN,
            MinimumLocalizationConfidence = 2d,
            MaximumRecognitionWindowMilliseconds = 100,
            FirstFloorConfirmationFrames = 0,
            SecondFloorConfirmationFrames = 99
        };

        tuning.Normalize();
        var clone = tuning.Clone();
        clone.FirstFloorConfirmationFrames = 8;

        Assert.Equal(0.60d, tuning.MinimumConfidence);
        Assert.Equal(0.99d, tuning.MinimumLocalizationConfidence);
        Assert.Equal(500, tuning.MaximumRecognitionWindowMilliseconds);
        Assert.Equal(1, tuning.FirstFloorConfirmationFrames);
        Assert.Equal(8, tuning.SecondFloorConfirmationFrames);
        Assert.Equal(1, tuning.FirstFloorConfirmationFrames);
    }

    [Fact]
    public void PlayerTrackingTuningNormalizesInvalidValues()
    {
        var tuning = new MapPlayerTrackingTuning
        {
            MinimumConfidence = double.PositiveInfinity,
            LocalSearchFailureLimit = 0,
            StaleHideMilliseconds = 10000
        };

        tuning.Normalize();

        Assert.Equal(0.70d, tuning.MinimumConfidence);
        Assert.Equal(1, tuning.LocalSearchFailureLimit);
        Assert.Equal(5000, tuning.StaleHideMilliseconds);
    }

    [Fact]
    public void LegacySettingsGetNewTuningDefaultsAndIgnoreRemovedFields()
    {
        var json = """
        {
          "SchemaVersion": 6,
          "SessionTuning": {
            "MediumConfidence": 0.62,
            "BackgroundValidationMilliseconds": 900,
            "RequiredReplacementAdvantage": 0.4
          }
        }
        """;

        var settings = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        settings.Normalize();

        Assert.Equal(8, settings.SchemaVersion);
        Assert.Equal(0.62d, settings.SessionTuning.MediumConfidence);
        Assert.Equal(0.60d, settings.FloorRecognitionTuning.MinimumConfidence);
        Assert.Equal(0.70d, settings.PlayerTrackingTuning.MinimumConfidence);
        Assert.DoesNotContain(
            "BackgroundValidationMilliseconds",
            JsonSerializer.Serialize(settings));
        Assert.DoesNotContain(
            "RequiredReplacementAdvantage",
            JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void ForceBestRecognitionResultPersistsWithRuntimeSettings()
    {
        var settings = new MapRuntimeSettings
        {
            RecognitionTuning = new MapRecognitionTuning
            {
                ForceBestRecognitionResult = true
            }
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.True(
            restored.RecognitionTuning.ForceBestRecognitionResult);
    }

    [Fact]
    public void LegacyRuntimeSettingsMigrateToUniformAndDisableForcedPresentation()
    {
        var settings = new MapRuntimeSettings
        {
            SchemaVersion = 2,
            OverlayAlignmentMode = MapOverlayAlignmentMode.IndependentAxes,
            RecognitionTuning = new MapRecognitionTuning
            {
                ForceBestRecognitionResult = true
            }
        };

        settings.Normalize();

        Assert.Equal(
            MapRuntimeSettings.CurrentSchemaVersion,
            settings.SchemaVersion);
        Assert.Equal(
            MapOverlayAlignmentMode.Uniform,
            settings.OverlayAlignmentMode);
        Assert.False(
            settings.RecognitionTuning.ForceBestRecognitionResult);
    }

    [Fact]
    public void VersionThreeSettingsKeepExistingRecognitionPreferences()
    {
        var settings = new MapRuntimeSettings
        {
            SchemaVersion = 3,
            RecognitionTuning = new MapRecognitionTuning
            {
                ForceBestRecognitionResult = true
            }
        };

        settings.Normalize();

        Assert.Equal(
            MapRuntimeSettings.CurrentSchemaVersion,
            settings.SchemaVersion);
        Assert.True(settings.RecognitionTuning.ForceBestRecognitionResult);
        Assert.False(settings.ControlPanelToggleBinding.IsConfigured);
    }

    [Fact]
    public async Task SettingsFileWithoutSchemaIsMigratedAndRewritten()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.Settings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "OverlayAlignmentMode": 1,
                  "RecognitionTuning": {
                    "ForceBestRecognitionResult": true
                  }
                }
                """);
            var repository = new MapRuntimeSettingsRepository(root);

            var settings = await repository.LoadAsync();

            Assert.Equal(
                MapRuntimeSettings.CurrentSchemaVersion,
                settings.SchemaVersion);
            Assert.Equal(
                MapOverlayAlignmentMode.Uniform,
                settings.OverlayAlignmentMode);
            Assert.False(
                settings.RecognitionTuning.ForceBestRecognitionResult);
            using var migrated = JsonDocument.Parse(
                await File.ReadAllTextAsync(path));
            Assert.Equal(
                MapRuntimeSettings.CurrentSchemaVersion,
                migrated.RootElement.GetProperty("SchemaVersion")
                    .GetInt32());
            Assert.False(
                migrated.RootElement.GetProperty("RecognitionTuning")
                    .GetProperty("ForceBestRecognitionResult")
                    .GetBoolean());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AlignmentCalibrationPersistsScaleAndSignatureWithoutTranslation()
    {
        var signature = DisplayTestMatrix.Baseline.CreateSignature();
        var mapId = Guid.NewGuid();
        var settings = new MapRuntimeSettings
        {
            AlignmentCalibrations =
            [
                new MapAlignmentCalibration
                {
                    MapId = mapId,
                    MapUpdatedAt = DateTimeOffset.UtcNow,
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
                }
            ]
        };

        var clone = settings.Clone();

        var calibration = Assert.Single(clone.AlignmentCalibrations);
        Assert.Equal(mapId, calibration.MapId);
        Assert.Equal(1.25d, calibration.UniformScale);
        Assert.DoesNotContain(
            "Translation",
            JsonSerializer.Serialize(calibration),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GameMapToggleStateAlternatesAndRejectsStaleOpenWork()
    {
        var state = new MapGameToggleState();

        var opened = state.Toggle();
        Assert.True(state.TryBeginOpenPipeline(opened));
        Assert.False(state.TryBeginOpenPipeline(opened));
        var closed = state.Toggle();

        Assert.True(opened.IsOpen);
        Assert.False(closed.IsOpen);
        Assert.False(state.TryBeginOpenPipeline(closed));
        Assert.False(state.IsCurrent(opened));
        Assert.True(state.IsCurrent(closed));

        var reopened = state.Toggle();

        Assert.True(state.TryBeginOpenPipeline(reopened));

        state.Reset();

        Assert.False(state.IsOpen);
        Assert.False(state.IsCurrent(closed));
        Assert.False(state.IsCurrent(reopened));
        Assert.False(state.TryBeginOpenPipeline(reopened));
    }

    [Fact]
    public void SuccessfulScanCanSynchronizeMapAsOpenBeforeNextToggle()
    {
        var state = new MapGameToggleState();

        state.MarkOpen();
        var closed = state.Toggle();

        Assert.False(closed.IsOpen);
        Assert.True(state.IsCurrent(closed));
        Assert.False(state.TryBeginOpenPipeline(closed));
    }

    [Fact]
    public void ExplicitScanSynchronizationInvalidatesEarlierOpenPipelineAndNextToggleCloses()
    {
        var state = new MapGameToggleState();
        var earlierOpen = state.Toggle();
        Assert.True(state.TryBeginOpenPipeline(earlierOpen));

        state.MarkOpen();
        var closed = state.Toggle();

        Assert.False(state.IsCurrent(earlierOpen));
        Assert.False(closed.IsOpen);
        Assert.True(state.IsCurrent(closed));
        Assert.False(state.TryBeginOpenPipeline(closed));
    }

    [Fact]
    public void FailedOpenPipelineCanBeClaimedAgainWithoutChangingOpenState()
    {
        var state = new MapGameToggleState();
        var opened = state.Toggle();

        Assert.True(state.TryBeginOpenPipeline(opened));
        state.ReleaseOpenPipeline();

        Assert.True(state.IsOpen);
        Assert.True(state.IsCurrent(opened));
        Assert.True(state.TryBeginOpenPipeline(opened));
        Assert.False(state.TryBeginOpenPipeline(opened));
    }

    [Fact]
    public void GameMapBindingIsClonedAndPreservedWhenDistinct()
    {
        var settings = new MapRuntimeSettings
        {
            GameMapToggleBinding = new MapInputBinding
            {
                Kind = MapInputBindingKind.Mouse,
                MouseButton = MapMouseButton.XButton1
            },
            QuickScanBinding = new MapInputBinding
            {
                Kind = MapInputBindingKind.Keyboard,
                VirtualKey = 113
            }
        };

        settings.Normalize();
        var clone = settings.Clone();
        clone.GameMapToggleBinding.MouseButton = MapMouseButton.XButton2;

        Assert.True(settings.GameMapToggleBinding.IsConfigured);
        Assert.Equal(
            MapMouseButton.XButton1,
            settings.GameMapToggleBinding.MouseButton);
        Assert.Equal(
            MapMouseButton.XButton2,
            clone.GameMapToggleBinding.MouseButton);
    }

    [Fact]
    public void ConflictingGameMapBindingIsClearedDuringSettingsRecovery()
    {
        var binding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 77
        };
        var settings = new MapRuntimeSettings
        {
            QuickScanBinding = binding.Clone(),
            GameMapToggleBinding = binding.Clone()
        };

        settings.Normalize();

        Assert.True(settings.QuickScanBinding.IsConfigured);
        Assert.False(settings.GameMapToggleBinding.IsConfigured);
    }

    [Fact]
    public void ControlPanelBindingIsUnconfiguredByDefaultAndClonesIndependently()
    {
        var settings = new MapRuntimeSettings();

        Assert.False(settings.ControlPanelToggleBinding.IsConfigured);

        settings.ControlPanelToggleBinding = new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = 114
        };
        var clone = settings.Clone();
        clone.ControlPanelToggleBinding.VirtualKey = 115;

        Assert.Equal(114u, settings.ControlPanelToggleBinding.VirtualKey);
        Assert.Equal(115u, clone.ControlPanelToggleBinding.VirtualKey);
    }

    [Fact]
    public void ConflictingNewControlPanelBindingDoesNotReplaceExistingBinding()
    {
        var existing = new MapInputBinding
        {
            Kind = MapInputBindingKind.Mouse,
            MouseButton = MapMouseButton.XButton2
        };
        var settings = new MapRuntimeSettings
        {
            GameMapToggleBinding = existing.Clone(),
            ControlPanelToggleBinding = existing.Clone()
        };

        settings.Normalize();

        Assert.True(settings.GameMapToggleBinding.IsConfigured);
        Assert.False(settings.ControlPanelToggleBinding.IsConfigured);
    }

    [Fact]
    public void ControlPanelBindingRoundTripsWithCurrentSchema()
    {
        var json = JsonSerializer.Serialize(new MapRuntimeSettings
        {
            ControlPanelToggleBinding = new MapInputBinding
            {
                Kind = MapInputBindingKind.Keyboard,
                VirtualKey = 116
            }
        });

        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.Equal(
            MapRuntimeSettings.CurrentSchemaVersion,
            restored.SchemaVersion);
        Assert.Equal(116u, restored.ControlPanelToggleBinding.VirtualKey);
    }

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
    public void ExplicitScanAndManualRecognitionAreNotGatedByFloor()
    {
        Assert.True(MapFloorRecognitionRules.RequiresConfirmedFirstFloor(
            MapFloorRecognitionIntent.AutomaticMapOpen));
        Assert.False(MapFloorRecognitionRules.RequiresConfirmedFirstFloor(
            MapFloorRecognitionIntent.QuickScan));
        Assert.False(MapFloorRecognitionRules.RequiresConfirmedFirstFloor(
            MapFloorRecognitionIntent.ManualRecognition));
        Assert.True(
            MapFloorRecognitionRules.GetOperationPriority(
                MapFloorRecognitionIntent.ManualRecognition)
            > MapFloorRecognitionRules.GetOperationPriority(
                MapFloorRecognitionIntent.QuickScan));
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
