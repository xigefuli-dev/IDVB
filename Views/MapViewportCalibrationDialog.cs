using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenCvSharp;
using Windows.Storage.Streams;
using Windows.UI;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace IDVBuff.Views;

internal sealed class MapViewportCalibrationDialog
{
    private readonly CapturedGameFrame _frame;
    private readonly string _title;
    private readonly string _instructions;
    private readonly Grid _surface = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 18, 24, 32))
    };
    private readonly Canvas _canvas = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255))
    };
    private ContentDialog? _dialog;
    private Point? _dragStart;
    private NormalizedRectangle? _region;

    private MapViewportCalibrationDialog(
        CapturedGameFrame frame,
        NormalizedRectangle? currentRegion,
        string title,
        string instructions)
    {
        _frame = frame;
        _region = currentRegion?.Clone();
        _title = title;
        _instructions = instructions;
    }

    public static async Task<NormalizedRectangle?> ShowAsync(
        XamlRoot xamlRoot,
        CapturedGameFrame frame,
        NormalizedRectangle? currentRegion,
        string title,
        string instructions)
    {
        var calibration = new MapViewportCalibrationDialog(
            frame,
            currentRegion,
            title,
            instructions);
        return await calibration.ShowCoreAsync(xamlRoot);
    }

    private async Task<NormalizedRectangle?> ShowCoreAsync(XamlRoot xamlRoot)
    {
        var imageRatio = (double)_frame.Image.Width / _frame.Image.Height;
        var surfaceWidthBudget = Math.Clamp(xamlRoot.Size.Width - 112d, 320d, 960d);
        var surfaceHeightBudget = Math.Clamp(xamlRoot.Size.Height - 280d, 240d, 620d);
        var surfaceWidth = Math.Min(surfaceWidthBudget, surfaceHeightBudget * imageRatio);
        var surfaceHeight = surfaceWidth / imageRatio;
        _surface.Width = surfaceWidth;
        _surface.Height = surfaceHeight;

        var bitmap = await CreateBitmapAsync(_frame.Image);
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        _surface.Children.Add(image);
        _surface.Children.Add(_canvas);
        _surface.SizeChanged += (_, _) => RenderRegion();
        _surface.PointerPressed += Surface_PointerPressed;
        _surface.PointerMoved += Surface_PointerMoved;
        _surface.PointerReleased += Surface_PointerReleased;
        _surface.PointerCanceled += Surface_PointerCanceled;

        var preview = new Viewbox
        {
            Child = _surface,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            MaxWidth = surfaceWidth,
            MaxHeight = surfaceHeight,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var content = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(new TextBlock
        {
            Text = _instructions,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(preview);
        var dialogWidth = Math.Min(xamlRoot.Size.Width - 32d, surfaceWidth + 64d);
        var dialogHeight = Math.Max(320d, xamlRoot.Size.Height - 32d);
        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = _title,
            Content = content,
            PrimaryButtonText = "保存校准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = _region?.IsValid is true
        };
        _dialog.Resources["ContentDialogMaxWidth"] = dialogWidth;
        _dialog.Resources["ContentDialogMaxHeight"] = dialogHeight;
        var result = await _dialog.ShowAsync();
        return result == ContentDialogResult.Primary && _region?.IsValid is true
            ? _region.Clone()
            : null;
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = ToNormalizedPoint(e.GetCurrentPoint(_surface).Position);
        if (point is null)
            return;
        _dragStart = point;
        _region = new NormalizedRectangle { X = point.Value.X, Y = point.Value.Y };
        _surface.CapturePointer(e.Pointer);
        RenderRegion();
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null)
            return;
        var point = ToNormalizedPoint(e.GetCurrentPoint(_surface).Position, clamp: true);
        if (point is null)
            return;
        _region = CreateRectangle(_dragStart.Value, point.Value);
        RenderRegion();
        e.Handled = true;
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null)
            return;
        var point = ToNormalizedPoint(e.GetCurrentPoint(_surface).Position, clamp: true);
        if (point is not null)
            _region = CreateRectangle(_dragStart.Value, point.Value);
        _dragStart = null;
        _surface.ReleasePointerCapture(e.Pointer);
        _dialog!.IsPrimaryButtonEnabled = _region?.IsValid is true;
        RenderRegion();
        e.Handled = true;
    }

    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = null;
        _surface.ReleasePointerCapture(e.Pointer);
        _dialog!.IsPrimaryButtonEnabled = _region?.IsValid is true;
        RenderRegion();
        e.Handled = true;
    }

    private Point? ToNormalizedPoint(Point point, bool clamp = false)
    {
        var visible = GetVisibleImageBounds();
        if (visible.Width <= 0d || visible.Height <= 0d)
            return null;
        if (!clamp
            && (point.X < visible.X
                || point.Y < visible.Y
                || point.X > visible.X + visible.Width
                || point.Y > visible.Y + visible.Height))
        {
            return null;
        }
        return new Point(
            Math.Clamp((point.X - visible.X) / visible.Width, 0d, 1d),
            Math.Clamp((point.Y - visible.Y) / visible.Height, 0d, 1d));
    }

    private Rect GetVisibleImageBounds()
    {
        if (_surface.ActualWidth <= 0d || _surface.ActualHeight <= 0d)
            return Rect.Empty;
        var imageRatio = (double)_frame.Image.Width / _frame.Image.Height;
        var surfaceRatio = _surface.ActualWidth / _surface.ActualHeight;
        if (surfaceRatio > imageRatio)
        {
            var width = _surface.ActualHeight * imageRatio;
            return new Rect((_surface.ActualWidth - width) / 2d, 0d, width, _surface.ActualHeight);
        }
        var height = _surface.ActualWidth / imageRatio;
        return new Rect(0d, (_surface.ActualHeight - height) / 2d, _surface.ActualWidth, height);
    }

    private void RenderRegion()
    {
        _canvas.Children.Clear();
        if (_region?.IsValid is not true)
            return;
        var visible = GetVisibleImageBounds();
        var rectangle = new Rectangle
        {
            Width = _region.Width * visible.Width,
            Height = _region.Height * visible.Height,
            Stroke = new SolidColorBrush(Color.FromArgb(255, 235, 55, 55)),
            StrokeThickness = 4d,
            Fill = new SolidColorBrush(Color.FromArgb(35, 235, 55, 55)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(rectangle, visible.X + (_region.X * visible.Width));
        Canvas.SetTop(rectangle, visible.Y + (_region.Y * visible.Height));
        _canvas.Children.Add(rectangle);
    }

    private static NormalizedRectangle CreateRectangle(Point start, Point end) => new()
    {
        X = Math.Min(start.X, end.X),
        Y = Math.Min(start.Y, end.Y),
        Width = Math.Abs(end.X - start.X),
        Height = Math.Abs(end.Y - start.Y)
    };

    private static async Task<BitmapImage> CreateBitmapAsync(Mat image)
    {
        Cv2.ImEncode(".png", image, out var bytes);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
