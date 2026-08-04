using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapRuntimeDefaultSettingsTests
{
    [Fact]
    public void NewSettingsUseTheSafeReleaseBaselineWithoutMachineSpecificData()
    {
        var settings = MapRuntimeSettings.CreateDefault();

        Assert.False(settings.IsEnabled);
        Assert.Equal(FirstScanStrategy.DoubleGate, settings.FirstScanStrategy);
        Assert.False(settings.CollectLogs);
        Assert.False(settings.CollectAlignmentResearchData);
        Assert.False(settings.SkipFloorRecognition);
        Assert.False(settings.AllowMapExtendBeyondBounds);
        Assert.False(settings.PersistentMiniMapEnabled);
        Assert.False(settings.PlayerTrackingEnabled);
        Assert.Equal(0.25d, settings.MiniMapScale);
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
        Assert.Equal(80, settings.RecognitionTuning.SideEntranceFeatureRadius);
        Assert.Equal(10, settings.SessionTuning.OpeningAnimationDelayMilliseconds);
        Assert.Equal(20, settings.SessionTuning.StableFrameIntervalMilliseconds);
        Assert.Equal(2, settings.SessionTuning.StableFrameCount);
        Assert.Equal(0.70d, settings.SessionTuning.HighConfidence);
        Assert.Equal(0.60d, settings.SessionTuning.MediumConfidence);
        Assert.Equal(2, settings.SessionTuning.MediumConfidenceFrames);
        Assert.True(settings.SessionTuning.SkipStabilityConfirmation);
        Assert.False(settings.StructureRegistrationTuning.UseAuxiliaryAnchorRecognition);
        Assert.False(settings.StructureRegistrationTuning.EnableEccRefinement);
        Assert.True(settings.StructureRegistrationTuning.EnableFastAlignment);
        Assert.Equal(0.40d, settings.StructureRegistrationTuning.MinimumEdgeCoverage);
        Assert.Equal(0.04d, settings.StructureRegistrationTuning.MinimumCandidateMargin);
        Assert.Equal(0.64d, settings.StructureRegistrationTuning.FeatureRatioThreshold);
    }
}
