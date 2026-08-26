using IDVBuff.Features.Maps;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;

namespace IDVBuff.Views;

internal sealed class DirectMapDisplayWindow
{
    private const int MinimumWidth = 240;
    private const int EstimatedTitleBarHeight = 32;
    private static readonly HashSet<DirectMapDisplayWindow> OpenWindows = [];

    private readonly Window _window;
    private readonly AppWindow _appWindow;
    private readonly MapRecord _map;
    private readonly MapRepository _repository;
    private readonly IReadOnlyList<FloorDefinition> _floors;
    private readonly Image _image;
    private int _floorIndex;
    private double _imageAspectRatio = 1.6d;
    private bool _adjustingBounds;
    private SizeInt32 _lastSize;

    private DirectMapDisplayWindow(
        MapRecord map,
        MapRepository repository,
        string initialFloorKey)
    {
        _map = map;
        _repository = repository;
        _floors = MapFloorRules.GetOrderedFloors(map);
        _floorIndex = Math.Max(0, _floors.ToList().FindIndex(
            floor => string.Equals(floor.Key, initialFloorKey, StringComparison.OrdinalIgnoreCase)));
        _window = new Window();
        _appWindow = _window.AppWindow;
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _window.Content = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
            Children =
            {
                _image
            }
        };
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.SetBorderAndTitleBar(true, true);
        }
        _appWindow.Changed += AppWindow_Changed;
        _window.Closed += Window_Closed;
        ShowFloor(_floorIndex, preservePosition: false);
    }

    public static Task ShowAsync(
        MapRecord map,
        MapRepository repository,
        string initialFloorKey)
    {
        var display = new DirectMapDisplayWindow(map, repository, initialFloorKey);
        OpenWindows.Add(display);
        display._window.Activate();
        display.PlaceInitialWindow();
        return Task.CompletedTask;
    }

    internal static void SwitchFloorForOpenWindows()
    {
        foreach (var display in OpenWindows.ToArray())
            display.SwitchFloor();
    }

    private void SwitchFloor()
    {
        if (_floors.Count < 2)
            return;
        for (var offset = 1; offset <= _floors.Count; offset++)
        {
            var next = (_floorIndex + offset) % _floors.Count;
            if (TryGetFloorImagePath(_floors[next].Key, out _))
            {
                ShowFloor(next, preservePosition: true);
                return;
            }
        }
    }

    private void ShowFloor(int index, bool preservePosition)
    {
        if (index < 0 || index >= _floors.Count
            || !TryGetFloorImagePath(_floors[index].Key, out var imagePath))
            return;
        _floorIndex = index;
        var floor = _floors[index];
        _window.Title = $"{_map.DisplayName} - {floor.DisplayName}";
        _image.Source = CreateBitmap(imagePath, preservePosition);
    }

    private bool TryGetFloorImagePath(string floorKey, out string path)
    {
        path = _repository.GetFloorOverlayPath(_map, floorKey);
        if (File.Exists(path))
            return true;
        path = _repository.GetFloorRecognitionPath(_map, floorKey);
        return File.Exists(path);
    }

    private BitmapImage CreateBitmap(string imagePath, bool preservePosition)
    {
        var bitmap = new BitmapImage(new Uri(imagePath));
        bitmap.ImageOpened += (_, _) =>
        {
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
                return;
            _imageAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            if (preservePosition)
            {
                EnforceAspectRatio(_appWindow.Size);
                KeepInsideWorkArea();
            }
            else
            {
                PlaceInitialWindow();
            }
        };
        return bitmap;
    }

    private void PlaceInitialWindow()
    {
        var workArea = GetWorkArea();
        var maximumWidth = Math.Max(MinimumWidth, workArea.Width - 32);
        var maximumContentHeight = Math.Max(120, workArea.Height - EstimatedTitleBarHeight - 32);
        double width = Math.Min(720, maximumWidth);
        var contentHeight = width / _imageAspectRatio;
        if (contentHeight > maximumContentHeight)
        {
            contentHeight = maximumContentHeight;
            width = contentHeight * _imageAspectRatio;
        }
        var size = new SizeInt32(
            Math.Max(MinimumWidth, (int)Math.Round(width)),
            (int)Math.Round(contentHeight + EstimatedTitleBarHeight));
        var x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
        ApplyBounds(new RectInt32(x, y, size.Width, size.Height));
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_adjustingBounds)
            return;
        if (args.DidSizeChange)
            EnforceAspectRatio(sender.Size);
        else if (args.DidPositionChange)
            KeepInsideWorkArea();
    }

    private void EnforceAspectRatio(SizeInt32 requested)
    {
        var workArea = GetWorkArea();
        var widthChanged = Math.Abs(requested.Width - _lastSize.Width)
            >= Math.Abs(requested.Height - _lastSize.Height);
        double width = Math.Clamp(requested.Width, MinimumWidth, workArea.Width);
        double contentHeight = Math.Max(1, requested.Height - EstimatedTitleBarHeight);
        if (widthChanged)
            contentHeight = width / _imageAspectRatio;
        else
            width = contentHeight * _imageAspectRatio;

        if (width > workArea.Width)
        {
            width = workArea.Width;
            contentHeight = width / _imageAspectRatio;
        }
        if (contentHeight + EstimatedTitleBarHeight > workArea.Height)
        {
            contentHeight = workArea.Height - EstimatedTitleBarHeight;
            width = contentHeight * _imageAspectRatio;
        }
        ApplyBounds(new RectInt32(
            _appWindow.Position.X,
            _appWindow.Position.Y,
            (int)Math.Round(width),
            (int)Math.Round(contentHeight + EstimatedTitleBarHeight)));
    }

    private void KeepInsideWorkArea()
    {
        var workArea = GetWorkArea();
        var size = _appWindow.Size;
        var x = Math.Clamp(_appWindow.Position.X, workArea.X, workArea.X + workArea.Width - size.Width);
        var y = Math.Clamp(_appWindow.Position.Y, workArea.Y, workArea.Y + workArea.Height - size.Height);
        if (x != _appWindow.Position.X || y != _appWindow.Position.Y)
            ApplyBounds(new RectInt32(x, y, size.Width, size.Height));
    }

    private RectInt32 GetWorkArea() =>
        DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;

    private void ApplyBounds(RectInt32 bounds)
    {
        _adjustingBounds = true;
        _appWindow.MoveAndResize(bounds);
        _lastSize = new SizeInt32(bounds.Width, bounds.Height);
        _adjustingBounds = false;
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _appWindow.Changed -= AppWindow_Changed;
        _window.Closed -= Window_Closed;
        OpenWindows.Remove(this);
    }
}
