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
    internal event Action<bool>? DisplayPreviewVisibilityChanged;
    internal event Action<OverlaySkeletonPreviewState>? DisplayPreviewChanged;

    private sealed record AlignmentModeChoice(
        MapOverlayAlignmentMode Mode,
        string DisplayName);


    public MapStatusPage()
    {
        try
        {
            BuildView();
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
