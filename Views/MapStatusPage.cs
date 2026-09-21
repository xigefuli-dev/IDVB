using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

/// <summary>Runtime control center for scanning, manual recognition, and overlay state.</summary>
public sealed partial class MapStatusPage : UserControl
{
    private readonly ScanModeSelector _scanModeSelector = new();
    private bool _savingScanMode;
    private ScanPerformanceMode? _requestedScanMode;
    private void AttachScanModeSelector()
    {
        if (_root?.Children[1] is not StackPanel content) return;
        content.Children.Insert(content.Children.IndexOf(_enabledToggle) + 1, _scanModeSelector);
        _scanModeSelector.ModeChanged += async mode =>
        {
            if (_refreshing) return;
            _requestedScanMode = mode;
            if (_savingScanMode) return;
            _savingScanMode = true;
            try
            {
                // Keep pointer capture while dragging; serialize saves and retain the last
                // requested position if another value arrives while storage is pending.
                while (_requestedScanMode is { } requested)
                {
                    _requestedScanMode = null;
                    try { await _runtime.SetScanPerformanceModeAsync(requested); }
                    catch (Exception exception) { _status.Text = $"扫描模式保存失败：{exception.Message}"; }
                }
            }
            finally
            {
                _savingScanMode = false;
                _scanModeSelector.SetMode(_runtime.Settings.ScanPerformanceMode, _runtime.Settings.SelectMapByTagsEnabled);
            }
        };
    }
    internal event Action<bool>? DisplayPreviewVisibilityChanged;
    internal event Action<OverlaySkeletonPreviewState>? DisplayPreviewChanged;

    private sealed record AlignmentModeChoice(
        MapOverlayAlignmentMode Mode,
        string DisplayName);

    private sealed record MapDecisionModeChoice(
        MapCandidateDecisionMode Mode,
        string DisplayName);


    public MapStatusPage()
    {
        try
        {
            BuildView();
            AttachTagSelectionToggle();
            AttachDiagnosticModeToggle();
            AttachMapLearningPanel();
            ApplySimplifiedOptions();
            AttachScanModeSelector();
            _viewBuilt = true;
        }
        catch (Exception exception)
        {
            ReportPageFailure("build", exception);
            Content = CreatePageFailureView(exception);
        }
        Loaded += MapStatusPage_Loaded;
        Unloaded += MapStatusPage_Unloaded;
    }

    private void AttachDiagnosticModeToggle()
    {
        if (_root is null
            || _root.Children.Count < 2
            || _root.Children[1] is not StackPanel content)
            return;
        var logsIndex = content.Children.IndexOf(_collectLogsToggle);
        content.Children.Insert(logsIndex >= 0 ? logsIndex + 1 : 0, _diagnosticModeToggle);
        _diagnosticModeToggle.Toggled += DiagnosticModeToggle_Toggled;
    }

    private void AttachTagSelectionToggle()
    {
        if (_root is null
            || _root.Children.Count < 2
            || _root.Children[1] is not StackPanel content)
            return;
        var backgroundScanIndex = content.Children.IndexOf(_backgroundScanToggle);
        content.Children.Insert(backgroundScanIndex >= 0 ? backgroundScanIndex + 1 : 0,
            _selectMapByTagsToggle);
        _selectMapByTagsToggle.Toggled += SelectMapByTags_Toggled;
    }

    private void ApplySimplifiedOptions()
    {
        if (_root is null
            || _root.Children.Count < 2
            || _root.Children[1] is not StackPanel content)
            return;

        foreach (var control in new UIElement[]
        {
            _firstScanStrategyToggle,
            _backgroundScanToggle,
            _silentScanToggle,
            _presetSelector,
            _allowAutomaticMapCacheToggle,
            _surveyStatusCard
        })
        {
            content.Children.Remove(control);
        }

        foreach (var button in content.Children
                     .OfType<Button>()
                     .Where(button => button.Content is "校准原生小地图区域" or "校准楼层显示区")
                     .ToArray())
        {
            content.Children.Remove(button);
        }

        foreach (var actions in content.Children.OfType<StackPanel>().ToArray())
        {
            actions.Children.Remove(_scanButton);
            actions.Children.Remove(_manualButton);
        }

        var displayPanel = content.Children
            .OfType<Expander>()
            .FirstOrDefault(expander => expander.Header is "显示与渲染")
            ?.Content as StackPanel;
        if (displayPanel is not null)
        {
            foreach (var control in new UIElement[]
            {
                _overlayStatusToggle,
                _reverseAlternateDisplayToggle,
                _allowExtendToggle,
                _miniMapEnabledToggle,
                _playerTrackingToggle,
                _forceBestResultToggle,
                _playerDecidesScaleToggle,
                _alignmentMode
            })
            {
                displayPanel.Children.Remove(control);
            }
        }

        var recognitionHeaderIndex = content.Children
            .IndexOf(content.Children.OfType<TextBlock>()
                .FirstOrDefault(text => text.Text == "识别参数"));
        while (recognitionHeaderIndex >= 0 && content.Children.Count > recognitionHeaderIndex)
            content.Children.RemoveAt(recognitionHeaderIndex);

        // Diagnostics are mounted as a separate grid row, not below the
        // recognition header. Remove that row as well so none of the retired
        // recognition/configuration detail leaks back onto the simplified page.
        if (_root.Children.Count > 2)
            _root.Children.RemoveAt(2);
    }

    private void MapStatusPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_subscribedToRuntime)
            {
                _runtime.StateChanged += Runtime_StateChanged;
                _subscribedToRuntime = true;
            }
            if (_viewBuilt)
                TryRefresh("loaded");
        }
        catch (Exception exception)
        {
            ReportPageFailure("loaded", exception);
        }
    }

    private void MapStatusPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _displayPreviewExpanded = false;
        DisplayPreviewVisibilityChanged?.Invoke(false);
        if (!_subscribedToRuntime)
            return;
        _runtime.StateChanged -= Runtime_StateChanged;
        _subscribedToRuntime = false;
    }
}
