using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    private OverlayPreviewPart _activeDisplayPreviewPart;

    private void DisplayExpander_Expanding(
        Expander sender,
        ExpanderExpandingEventArgs e)
    {
        _displayPreviewExpanded = true;
        _activeDisplayPreviewPart = OverlayPreviewPart.None;
        PublishDisplayPreview(OverlayPreviewPart.None);
        DisplayPreviewVisibilityChanged?.Invoke(true);
    }

    private void DisplayExpander_Collapsed(
        Expander sender,
        ExpanderCollapsedEventArgs e)
    {
        _displayPreviewExpanded = false;
        _activeDisplayPreviewPart = OverlayPreviewPart.None;
        DisplayPreviewVisibilityChanged?.Invoke(false);
    }

    private void PublishDisplayPreview(OverlayPreviewPart activePart)
    {
        if (!_displayPreviewExpanded)
            return;
        if (activePart != OverlayPreviewPart.None)
            _activeDisplayPreviewPart = activePart;

        var availablePresets = _runtime.GetAvailablePresets();
        var selectedPresetName = _runtime.GetSelectedResolutionPreset();
        var previewPresetName = string.IsNullOrWhiteSpace(selectedPresetName)
            ? _runtime.GetActivePreset()
            : selectedPresetName;
        var previewPreset = availablePresets.FirstOrDefault(profile =>
            string.Equals(profile.Name, previewPresetName, StringComparison.Ordinal));
        var resolutionWidth = previewPreset?.ClientWidth > 0
            ? previewPreset.ClientWidth
            : _runtime.Settings.CalibrationClientWidth > 0
                ? _runtime.Settings.CalibrationClientWidth
                : 1920;
        var resolutionHeight = previewPreset?.ClientHeight > 0
            ? previewPreset.ClientHeight
            : _runtime.Settings.CalibrationClientHeight > 0
                ? _runtime.Settings.CalibrationClientHeight
                : 1080;
        var miniMapScale = Math.Clamp(_miniMapScaleSlider.Value / 100d, 0d, 1d);
        var miniMapPixelSize = _runtime.CurrentMiniMapPixelSize is { } currentSize
            && _runtime.CurrentMiniMapScale is { } currentScale
            && currentScale > 0d
                ? (
                    Width: currentSize.Width * miniMapScale / currentScale,
                    Height: currentSize.Height * miniMapScale / currentScale)
                : (Width: 1800d * miniMapScale, Height: 1400d * miniMapScale);
        DisplayPreviewChanged?.Invoke(new OverlaySkeletonPreviewState(
            resolutionWidth,
            resolutionHeight,
            _activeDisplayPreviewPart,
            Math.Clamp(_statusOpacitySlider.Value / 100d, 0d, 1d),
            Math.Clamp(_statusScaleSlider.Value / 100d, 0d, 1d),
            Math.Clamp(_statusOffsetXSlider.Value / 100d, 0d, 1d),
            Math.Clamp(_statusOffsetYSlider.Value / 100d, 0d, 1d),
            Math.Clamp(_miniMapOpacitySlider.Value / 100d, 0d, 1d),
            Math.Clamp(_miniMapOffsetXSlider.Value / 100d, 0d, 1d),
            Math.Clamp(_miniMapOffsetYSlider.Value / 100d, 0d, 1d),
            miniMapPixelSize.Width,
            miniMapPixelSize.Height,
            _showGateMarkersOnMiniMapToggle.IsOn && _showGateMarkersToggle.IsOn,
            _showAuxiliaryAnchorsOnMiniMapToggle.IsOn && _showAuxiliaryAnchorsToggle.IsOn,
            _showTextAnnotationsOnMiniMapToggle.IsOn && _showTextAnnotationsToggle.IsOn,
            _showBoxAnnotationsOnMiniMapToggle.IsOn && _showBoxAnnotationsToggle.IsOn,
            _showLineAnnotationsOnMiniMapToggle.IsOn && _showLineAnnotationsToggle.IsOn,
            _showFloorOnMiniMapToggle.IsOn));
    }
}
