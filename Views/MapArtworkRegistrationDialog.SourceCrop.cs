using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace IDVBuff.Views;

internal sealed partial class MapArtworkRegistrationDialog
{
    private readonly Button _cropButton = new() { Content = "选择本层范围（点两角）" };
    private readonly Button _wholeArtworkButton = new() { Content = "使用整张图" };
    private bool _selectingSourceCrop;
    private Point? _sourceCropStart;

    private StackPanel CreateSourceCropControls()
    {
        _cropButton.Click += (_, _) =>
        {
            _selectingSourceCrop = !_selectingSourceCrop;
            _sourceCropStart = null;
            _pendingSource = null;
            _cropButton.Content = _selectingSourceCrop ? "取消选择范围" : "选择本层范围（点两角）";
            PointsChanged();
            if (_selectingSourceCrop) _status.Text = "在左侧作者原图点出本层范围的两个对角；原图会完整保留。";
        };
        _wholeArtworkButton.Click += (_, _) =>
        {
            _selectingSourceCrop = false;
            _sourceCropStart = null;
            _sourceCropRegion = null;
            _sourceCropPoints.Clear();
            _cropButton.Content = "选择本层范围（点两角）";
            PointsChanged();
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(_cropButton);
        controls.Children.Add(_wholeArtworkButton);
        return controls;
    }

    private void SelectSourceCropCorner(Point pixel)
    {
        if (_sourceCropStart is not { } start)
        {
            _sourceCropStart = pixel;
            RenderDots();
            _status.Text = "再点本层范围的另一个对角。";
            return;
        }
        var region = new NormalizedRectangle
        {
            X = Math.Min(start.X, pixel.X) / _sourceWidth,
            Y = Math.Min(start.Y, pixel.Y) / _sourceHeight,
            Width = Math.Abs(start.X - pixel.X) / _sourceWidth,
            Height = Math.Abs(start.Y - pixel.Y) / _sourceHeight
        };
        if (!region.IsValid)
        {
            _status.Text = "范围太小，请再点一个能包含本层地图的对角。";
            return;
        }
        _sourceCropRegion = region;
        _sourceCropPoints.Clear();
        _sourceCropStart = null;
        _selectingSourceCrop = false;
        _cropButton.Content = "选择本层范围（点两角）";
        PointsChanged();
    }

    private void RenderSourceCrop(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var outline = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 32, 220, 220)),
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        var points = _sourceCropPoints.Count >= 3 ? _sourceCropPoints : RectanglePoints(_sourceCropRegion);
        foreach (var point in points)
            outline.Points.Add(new Point(bounds.X + point.X * bounds.Width, bounds.Y + point.Y * bounds.Height));
        if (outline.Points.Count > 0) outline.Points.Add(outline.Points[0]);
        _sourceDots.Children.Add(outline);
        if (_sourceCropStart is { } start)
            AddDot(_sourceDots, bounds, start.X / _sourceWidth, start.Y / _sourceHeight, 1);
    }

    private static List<NormalizedPoint> RectanglePoints(NormalizedRectangle? region) => region is null ? [] :
    [
        new() { X = region.X, Y = region.Y },
        new() { X = region.X + region.Width, Y = region.Y },
        new() { X = region.X + region.Width, Y = region.Y + region.Height },
        new() { X = region.X, Y = region.Y + region.Height }
    ];
}
