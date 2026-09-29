using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapRuntimeDefaultSettingsTests
{
    [Theory]
    [InlineData("{\"SchemaVersion\":21,\"DiagnosticModeEnabled\":false}")]
    [InlineData("{\"DiagnosticModeEnabled\":false}")]
    [InlineData("{\"SchemaVersion\":21,\"DiagnosticModeEnabled\":true}")]
    public async Task Version166EnablesDiagnosticsOnceAndPreservesLaterOptOut(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Settings.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(path, json);
            var repository = new MapRuntimeSettingsRepository(root);
            var migrated = await repository.LoadAsync();
            Assert.True(migrated.DiagnosticModeEnabled);
            using (var persisted = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Assert.Equal(22, persisted.RootElement.GetProperty("SchemaVersion").GetInt32());
                Assert.True(persisted.RootElement.GetProperty("DiagnosticModeEnabled").GetBoolean());
            }

            var restarted = await new MapRuntimeSettingsRepository(root).LoadAsync();
            Assert.True(restarted.DiagnosticModeEnabled);
            restarted.DiagnosticModeEnabled = false;
            await repository.SaveAsync(restarted.Clone());
            var optedOut = await new MapRuntimeSettingsRepository(root).LoadAsync();
            optedOut.Normalize();
            Assert.False(optedOut.DiagnosticModeEnabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FreshSettingsEnableDiagnosticsAndAllowOptOut()
    {
        Assert.True(new MapRuntimeSettings().DiagnosticModeEnabled);
        var root = Path.Combine(Path.GetTempPath(), $"IDVBuff.Settings.{Guid.NewGuid():N}");
        try
        {
            var repository = new MapRuntimeSettingsRepository(root);
            var settings = await repository.LoadAsync();
            Assert.True(settings.DiagnosticModeEnabled);
            settings.DiagnosticModeEnabled = false;
            await repository.SaveAsync(settings);
            Assert.False((await repository.LoadAsync()).DiagnosticModeEnabled);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NewSettingsUseTheSafeReleaseBaselineWithoutMachineSpecificData()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        Assert.Equal(22, settings.SchemaVersion);
        Assert.True(settings.DiagnosticModeEnabled);
        Assert.Equal(ScanPerformanceMode.Balanced, settings.ScanPerformanceMode);
        Assert.False(settings.IsEnabled);
        Assert.Equal(FirstScanStrategy.SideEntrance, settings.FirstScanStrategy);
        Assert.False(settings.BackgroundScanEnabled);
        Assert.True(settings.RequireStrictStructureRegistrationDuringScan);
        Assert.False(settings.EnableContinuousAlignment);
        Assert.False(settings.CollectLogs);
        Assert.False(settings.CollectAlignmentResearchData);
        Assert.False(settings.ContinuousMapLearningEnabled);
        Assert.False(settings.AutomaticMapModelTrainingEnabled);
        Assert.Null(settings.LastSelectedMapClass);
        Assert.False(settings.SkipFloorRecognition);
        Assert.True(settings.AllowMapExtendBeyondBounds);
        Assert.True(settings.PersistentMiniMapEnabled);
        Assert.False(settings.PlayerTrackingEnabled);
        Assert.True(settings.ShowRoutes);
        Assert.True(settings.ShowGateMarkers);
        Assert.False(settings.ShowAuxiliaryAnchors);
        Assert.False(settings.ShowGateMarkersOnMiniMap);
        Assert.False(settings.ShowAuxiliaryAnchorsOnMiniMap);
        Assert.True(settings.ShowLineAnnotations);
        Assert.True(settings.ShowLineAnnotationsOnMiniMap);
        Assert.Equal(0.25d, settings.MiniMapScale);
        Assert.Equal(0.46d, settings.MapOpacity);
        Assert.Equal(1.0d, settings.StatusOpacity);
        Assert.Equal(1.0d, settings.StatusScale);
        Assert.Equal(0d, settings.StatusOffsetY);
        Assert.Equal(0.55d, settings.MiniMapOpacity);
        Assert.Equal(0d, settings.MiniMapOffsetY);
        Assert.False(settings.QuickScanBinding.IsConfigured);
        Assert.False(settings.OverlayToggleBinding.IsConfigured);
        Assert.False(settings.GameMapToggleBinding.IsConfigured);
        Assert.False(settings.ControlPanelToggleBinding.IsConfigured);
        Assert.False(settings.ManualRecognitionBinding.IsConfigured);
        Assert.False(settings.SwitchFloorBinding.IsConfigured);
        Assert.False(settings.SaveMapCacheBinding.IsConfigured);
        Assert.False(settings.RestMapDisplayBinding.IsConfigured);
        Assert.False(settings.HideAlignmentResultBinding.IsConfigured);
        Assert.False(settings.AllowAutomaticMapCache);
        Assert.Empty(settings.AlignmentCalibrations);
        Assert.Empty(settings.FloorScaleCalibrations);
        Assert.Null(settings.MapViewportRegion);
        Assert.Null(settings.FloorDisplayRegion);
    }

    [Fact]
    public void NewTuningSettingsUseTheStatusPageBaseline()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        Assert.Equal(0.15d, settings.RecognitionTuning.VectorErrorTolerance);
        Assert.False(settings.RecognitionTuning.ForceBestRecognitionResult);
        Assert.False(settings.RecognitionTuning.ForceCandidateSelection);
        Assert.Equal(10, settings.SessionTuning.OpeningAnimationDelayMilliseconds);
        Assert.Equal(10, settings.SessionTuning.StableFrameIntervalMilliseconds);
        Assert.Equal(3, settings.SessionTuning.StableFrameCount);
        Assert.Equal(0.005d, settings.SessionTuning.StableFrameDifference);
        Assert.Equal(0.70d, settings.SessionTuning.HighConfidence);
        Assert.Equal(0.60d, settings.SessionTuning.MediumConfidence);
        Assert.Equal(2, settings.SessionTuning.MediumConfidenceFrames);
        Assert.False(settings.SessionTuning.SkipStabilityConfirmation);
        Assert.Equal(
            MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly,
            settings.StructureRegistrationTuning.AuxiliaryAnchorMode);
        Assert.False(settings.StructureRegistrationTuning.EnableEccRefinement);
        Assert.True(settings.StructureRegistrationTuning.EnableFastAlignment);
        Assert.Equal(0.40d, settings.StructureRegistrationTuning.MinimumEdgeCoverage);
        Assert.Equal(0.04d, settings.StructureRegistrationTuning.MinimumCandidateMargin);
        Assert.Equal(0.64d, settings.StructureRegistrationTuning.FeatureRatioThreshold);
        Assert.True(settings.StructureRegistrationTuning.UsePrebuiltStructureLine);
        settings.StructureRegistrationTuning.UsePrebuiltStructureLine = false;
        Assert.False(settings.Clone().StructureRegistrationTuning.UsePrebuiltStructureLine);
    }

    [Fact]
    public void LegacyStabilityDefaultsMigrateToStableThreeFrameCapture()
    {
        var tuning = new MapSessionTuning
        {
            SchemaVersion = 2,
            StableFrameCount = 2,
            StableFrameDifference = 0.015d,
            SkipStabilityConfirmation = true
        };

        tuning.Normalize();

        Assert.Equal(MapSessionTuning.CurrentSchemaVersion, tuning.SchemaVersion);
        Assert.Equal(3, tuning.StableFrameCount);
        Assert.Equal(0.005d, tuning.StableFrameDifference);
        Assert.False(tuning.SkipStabilityConfirmation);
    }

    [Fact]
    public void CustomizedLegacyStabilityTupleIsPreserved()
    {
        var tuning = new MapSessionTuning
        {
            SchemaVersion = 2,
            StableFrameCount = 4,
            StableFrameDifference = 0.015d,
            SkipStabilityConfirmation = true
        };

        tuning.Normalize();

        Assert.Equal(4, tuning.StableFrameCount);
        Assert.Equal(0.015d, tuning.StableFrameDifference);
        Assert.True(tuning.SkipStabilityConfirmation);
    }

    [Fact]
    public void LegacyDefaultFrameIntervalMigratesToTenMilliseconds()
    {
        var tuning = new MapSessionTuning
        {
            SchemaVersion = 3,
            StableFrameIntervalMilliseconds = 20,
            StableFrameCount = 3,
            StableFrameDifference = 0.005d,
            SkipStabilityConfirmation = false
        };

        tuning.Normalize();

        Assert.Equal(MapSessionTuning.CurrentSchemaVersion, tuning.SchemaVersion);
        Assert.Equal(10, tuning.StableFrameIntervalMilliseconds);
    }

    [Fact]
    public void CustomizedFrameIntervalIsPreservedDuringMigration()
    {
        var tuning = new MapSessionTuning
        {
            SchemaVersion = 3,
            StableFrameIntervalMilliseconds = 30,
            StableFrameCount = 3,
            StableFrameDifference = 0.005d,
            SkipStabilityConfirmation = false
        };

        tuning.Normalize();

        Assert.Equal(MapSessionTuning.CurrentSchemaVersion, tuning.SchemaVersion);
        Assert.Equal(30, tuning.StableFrameIntervalMilliseconds);
    }

    [Fact]
    public void LegacyAuxiliarySettingMigratesToAmbiguityOnly()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            SchemaVersion = 6,
            UseAuxiliaryAnchorRecognition = false
        };

        tuning.Normalize();

        Assert.Equal(
            MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly,
            tuning.AuxiliaryAnchorMode);
        Assert.False(tuning.ShouldUseAuxiliaryAnchors(isAmbiguous: false));
        Assert.True(tuning.ShouldUseAuxiliaryAnchors(isAmbiguous: true));
    }
}
