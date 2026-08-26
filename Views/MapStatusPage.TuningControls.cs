using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage : UserControl
{
    private async void PresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (_presetSelector.SelectedItem is not PresetSelectionItem item) return;
        try
        {
            await _runtime.SetSelectedResolutionPresetAsync(item.ProfileName);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }
        Refresh();
    }

    private async void SkipFloorRecognition_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try        {
            await _runtime.SetSkipFloorRecognitionAsync(
                _skipFloorRecognitionToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void SkipStabilityConfirmation_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetSkipStabilityConfirmationAsync(
                _skipStabilityConfirmationToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AllowExtendToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetAllowMapExtendBeyondBoundsAsync(_allowExtendToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void MiniMapEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetPersistentMiniMapEnabledAsync(_miniMapEnabledToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void PlayerTracking_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetPlayerTrackingEnabledAsync(_playerTrackingToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AlignmentMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_refreshing
            || _alignmentMode.SelectedItem is not AlignmentModeChoice choice)
        {
            return;
        }
        try
        {
            await _runtime.SetOverlayAlignmentModeAsync(choice.Mode);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void Tuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveRecognitionTuningAsync();

    private async void SessionTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue))
            return;
        try
        {
            await SaveSessionTuningAsync();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void FloorTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveFloorTuningAsync();

    private async void PlayerTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SavePlayerTuningAsync();

    private async void RecognitionTuning_Changed(
        object sender,
        RoutedEventArgs e) =>
        await SaveRecognitionTuningAsync();

    private async Task SaveRecognitionTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.RecognitionTuning.Clone();
            tuning.GateTemplateThreshold = _gateThreshold.Value / 100d;
            tuning.MinimumConfidence = _minimumConfidence.Value / 100d;
            tuning.VectorErrorTolerance = _vectorTolerance.Value;
            tuning.AmbiguityMargin = _ambiguityMargin.Value;
            tuning.ConfirmationAdvantage = _confirmationAdvantage.Value;
            tuning.ForceBestRecognitionResult = _forceBestResultToggle.IsOn;
            tuning.ForceCandidateSelection = _forceCandidateToggle.IsOn;
            tuning.PlayerDecidesScale = _playerDecidesScaleToggle.IsOn;
            tuning.WarmGateSearchBudgetMs = (int)Math.Round(_warmGateSearchBudget.Value);
            tuning.ConfirmationGateSearchBudgetMs =
                (int)Math.Round(_confirmationGateSearchBudget.Value);
            tuning.ConfirmationRoiTemplatePaddingFactor =
                _confirmationRoiPaddingFactor.Value;
            tuning.ConfirmationRoiMinimumPaddingPixels =
                (int)Math.Round(_confirmationRoiMinimumPadding.Value);
            tuning.ConfirmationMaximumMapDragPixelsPerSecond =
                _confirmationMaximumMapDrag.Value;
            await _runtime.SetRecognitionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SaveSessionTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.SessionTuning.Clone();
            tuning.HighConfidence = _highConfidenceThreshold.Value / 100d;
            tuning.MediumConfidence = _mediumConfidenceThreshold.Value / 100d;
            tuning.OpeningAnimationDelayMilliseconds =
                (int)Math.Round(_openingAnimationDelay.Value);
            tuning.OpeningTimeoutMilliseconds =
                (int)Math.Round(_openingTimeout.Value);
            tuning.StableFrameCount = (int)Math.Round(_stableFrameCount.Value);
            tuning.StableFrameIntervalMilliseconds =
                (int)Math.Round(_stableFrameInterval.Value);
            tuning.StableFrameDifference = _stableFrameDifference.Value;
            tuning.MediumConfidenceFrames =
                (int)Math.Round(_mediumConfidenceFrames.Value);
            tuning.CandidateStabilityPixels = _candidateStabilityPixels.Value;
            tuning.NativeScaleChangeRatio = _nativeScaleChangeRatio.Value / 100d;
            await _runtime.SetSessionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SaveFloorTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            await _runtime.SetFloorRecognitionTuningAsync(new MapFloorRecognitionTuning
            {
                MinimumConfidence = _floorMinimumConfidence.Value / 100d,
                MinimumLocalizationConfidence =
                    _floorLocalizationConfidence.Value / 100d,
                MaximumRecognitionWindowMilliseconds =
                    (int)Math.Round(_floorRecognitionTimeout.Value),
                FirstFloorConfirmationFrames =
                    (int)Math.Round(_floorFirstConfirmationFrames.Value),
                SecondFloorConfirmationFrames =
                    (int)Math.Round(_floorSecondConfirmationFrames.Value)
            });
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SavePlayerTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            await _runtime.SetPlayerTrackingTuningAsync(new MapPlayerTrackingTuning
            {
                MinimumConfidence = _playerMinimumConfidence.Value / 100d,
                LocalSearchFailureLimit = (int)Math.Round(_playerFailureLimit.Value),
                StaleHideMilliseconds =
                    (int)Math.Round(_playerStaleHideMilliseconds.Value)
            });
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void StructureTuning_Changed(object sender, RoutedEventArgs e) =>
        await SaveStructureTuningAsync();

    private async void StructureTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveStructureTuningAsync();

    private async Task SaveStructureTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.StructureRegistrationTuning.Clone();
            tuning.AuxiliaryAnchorMode = !_auxiliaryAnchorToggle.IsOn
                ? MapAuxiliaryAnchorRecognitionMode.Off
                : tuning.AuxiliaryAnchorMode
                    == MapAuxiliaryAnchorRecognitionMode.Always
                    ? MapAuxiliaryAnchorRecognitionMode.Always
                    : MapAuxiliaryAnchorRecognitionMode.AmbiguityOnly;
            tuning.ReusePreviousAlignmentResult =
                _reusePreviousAlignmentToggle.IsOn;
            if (double.IsFinite(_previousAlignmentSearchRadius.Value))
            {
                tuning.PreviousAlignmentSearchRadiusPixels =
                    (int)Math.Round(_previousAlignmentSearchRadius.Value);
            }
            tuning.EnableEccRefinement = _structureEccToggle.IsOn;
            tuning.EnableDebugOutput = _structureDebugToggle.IsOn;
            tuning.MinimumEdgeCoverage = _structureCoverage.Value / 100d;
            tuning.MinimumCandidateMargin = _structureMargin.Value / 100d;
            if (double.IsFinite(_auxiliaryMaxTemplates.Value))
            {
                tuning.MaximumAuxiliaryTemplates =
                    (int)Math.Round(_auxiliaryMaxTemplates.Value);
            }
            if (double.IsFinite(_auxiliaryDirectLockConfidence.Value))
                tuning.AuxiliaryDirectLockConfidence =
                    _auxiliaryDirectLockConfidence.Value / 100d;
            if (double.IsFinite(_viewportEdgeMargin.Value))
                tuning.MapViewportEdgeMargin =
                    _viewportEdgeMargin.Value / 100d;
            tuning.EnableFeatureVoting = _featureVotingToggle.IsOn;
            tuning.EnableVisibleMask = _visibleAwareToggle.IsOn;
            tuning.EnableVisibleAwareInjection = _visibleAwareToggle.IsOn;
            tuning.EnableFastAlignment = _fastAlignmentToggle.IsOn;
            tuning.FastAlignmentShadowMode = _fastShadowToggle.IsOn;
            if (double.IsFinite(_featureRatioThreshold.Value))
                tuning.FeatureRatioThreshold = _featureRatioThreshold.Value / 100d;
            if (double.IsFinite(_featureInlierTolerance.Value))
                tuning.FeatureInlierTolerancePixels = _featureInlierTolerance.Value;
            if (double.IsFinite(_featureMaxCandidates.Value))
            {
                tuning.MaximumTranslationCandidates =
                    (int)Math.Round(_featureMaxCandidates.Value);
            }
            if (double.IsFinite(_fastCoarseDownsample.Value))
            {
                tuning.FastCoarseDownsampleFactor =
                    (int)Math.Round(_fastCoarseDownsample.Value);
            }
            if (double.IsFinite(_fastCoarseTopK.Value))
            {
                tuning.FastCoarseTopK = (int)Math.Round(_fastCoarseTopK.Value);
            }
            if (double.IsFinite(_structureOccupancy.Value))
                tuning.MinimumOccupancyCoverage = _structureOccupancy.Value / 100d;
            if (double.IsFinite(_structurePartitions.Value))
            {
                tuning.MinimumConsistentPartitions =
                    (int)Math.Round(_structurePartitions.Value);
            }
            if (double.IsFinite(_structureEdgeTolerance.Value))
                tuning.EdgeDistanceTolerancePixels = _structureEdgeTolerance.Value;
            if (double.IsFinite(_structureTopCandidates.Value))
            {
                tuning.TopCandidateCount =
                    (int)Math.Round(_structureTopCandidates.Value);
            }
            if (double.IsFinite(_structureBudget.Value))
            {
                tuning.StructureFallbackBudgetMilliseconds =
                    (int)Math.Round(_structureBudget.Value);
            }
            await _runtime.SetStructureRegistrationTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

}
