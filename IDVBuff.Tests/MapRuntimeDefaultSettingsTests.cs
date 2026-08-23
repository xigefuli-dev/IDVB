using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapRuntimeDefaultSettingsTests
{
    [Fact]
    public void NewSettingsUseTheSafeReleaseBaselineWithoutMachineSpecificData()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        Assert.Equal(13, settings.SchemaVersion);
        Assert.False(settings.IsEnabled);
        Assert.Equal(FirstScanStrategy.SideEntrance, settings.FirstScanStrategy);
        Assert.False(settings.BackgroundScanEnabled);
        Assert.False(settings.CollectLogs);
        Assert.False(settings.CollectAlignmentResearchData);
        Assert.Null(settings.LastSelectedMapClass);
        Assert.False(settings.SkipFloorRecognition);
        Assert.False(settings.AllowMapExtendBeyondBounds);
        Assert.False(settings.PersistentMiniMapEnabled);
        Assert.False(settings.PlayerTrackingEnabled);
        Assert.True(settings.ShowLineAnnotations);
        Assert.True(settings.ShowLineAnnotationsOnMiniMap);
        Assert.Equal(0.25d, settings.MiniMapScale);
        Assert.Equal(0.46d, settings.MapOpacity);
        Assert.Equal(1.0d, settings.StatusOpacity);
        Assert.Equal(0d, settings.StatusOffsetY);
        Assert.Equal(0.55d, settings.MiniMapOpacity);
        Assert.Equal(50d, settings.MiniMapOffsetY);
        Assert.False(settings.QuickScanBinding.IsConfigured);
        Assert.False(settings.OverlayToggleBinding.IsConfigured);
        Assert.False(settings.GameMapToggleBinding.IsConfigured);
        Assert.False(settings.ControlPanelToggleBinding.IsConfigured);
        Assert.False(settings.ManualRecognitionBinding.IsConfigured);
        Assert.False(settings.SwitchFloorBinding.IsConfigured);
        Assert.False(settings.SaveMapCacheBinding.IsConfigured);
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
        Assert.True(settings.RecognitionTuning.ForceCandidateSelection);
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
