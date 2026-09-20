using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace IDVBuff.Views;
public sealed partial class MapListPage : UserControl
{

    private void AttachMarkerHostScroller()
    {
        if (_markerSurface is null)
            return;

        var hostScroller = FindAncestorScrollViewer(_markerSurface);
        if (ReferenceEquals(_markerHostScroller, hostScroller))
            return;

        DetachMarkerHostScroller();
        _markerHostScroller = hostScroller;
        if (_markerHostScroller is null)
            return;

        _markerHostScroller.ViewChanged += MarkerHostScroller_ViewChanged;
        _markerHostScroller.SizeChanged += MarkerHostScroller_SizeChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CloseHoldPreviewImmediately();
        ResetMarkerEditorSession();
        _previewImages.Clear();
        _workflowHost.Content = null;
        DetachMarkerHostScroller();
        if (ParentScrollViewer is not null)
            ParentScrollViewer.SizeChanged -= OnParentScrollViewerSizeChanged;
    }

    private void DetachMarkerHostScroller()
    {
        if (_markerHostScroller is null)
            return;

        _markerHostScroller.ViewChanged -= MarkerHostScroller_ViewChanged;
        _markerHostScroller.SizeChanged -= MarkerHostScroller_SizeChanged;
        _markerHostScroller = null;
    }

    private void MarkerHostScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
        PositionMarkerControlPanel();

    private void MarkerHostScroller_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PositionMarkerControlPanel();

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is ScrollViewer scrollViewer)
                return scrollViewer;
        }

        return null;
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        var left = Math.Max(first.Left, second.Left);
        var top = Math.Max(first.Top, second.Top);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : Rect.Empty;
    }
}
