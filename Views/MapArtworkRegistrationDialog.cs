using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OpenCvSharp;
using Windows.Storage.Streams;
using Windows.UI;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace IDVBuff.Views;

/// <summary>Maker-only, pixel-local registration of one artwork to one floor.</summary>
internal sealed partial class MapArtworkRegistrationDialog
{
    private readonly string _artworkPath;
    private readonly List<MapArtworkLandmark> _points = [];
    private readonly Grid _sourceSurface = CreateSurface();
    private readonly Grid _referenceSurface = CreateSurface();
    private readonly Canvas _sourceDots = new() { IsHitTestVisible = false };
    private readonly Canvas _referenceDots = new() { IsHitTestVisible = false };
    private readonly Image _previewArtwork = new() { Stretch = Stretch.Uniform, Opacity = 0.5 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _residual = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _fitButton = new() { Content = "计算并预览", IsEnabled = false };
    private readonly Button _undoButton = new() { Content = "撤销", IsEnabled = false };
    private readonly Button _resetButton = new() { Content = "重置", IsEnabled = false };
    private readonly ComboBox _fitMethod = new()
    {
        Header = "对齐方式",
        ItemsSource = new[] { "整体平均偏差最小", "优先控制最偏的墙角" },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private ContentDialog? _dialog;
    private MapArtworkRegistration? _fitted;
    private Point? _pendingSource;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _referenceWidth;
    private int _referenceHeight;
    private int _editVersion;
    private bool _busy;
    private bool _closed;
    private string _canonicalPath = string.Empty;
    private NormalizedRectangle? _sourceCropRegion;
    private List<NormalizedPoint> _sourceCropPoints = [];

    private MapArtworkRegistrationDialog(string artworkPath, FloorRecognitionProfile? artworkProfile)
    {
        _artworkPath = artworkPath;
        _sourceCropRegion = artworkProfile?.RecognitionRegion?.Clone();
        _sourceCropPoints = (artworkProfile?.FreeCropPoints ?? []).Select(point => point.Clone()).ToList();
    }

    internal static async Task<MapArtworkRegistration?> ShowAsync(
        XamlRoot xamlRoot,
        string artworkPath,
        string canonicalPath,
        MapArtworkRegistration? current = null,
        CancellationToken cancellationToken = default,
        FloorRecognitionProfile? artworkProfile = null)
    {
        var editor = new MapArtworkRegistrationDialog(artworkPath, artworkProfile);
        return await editor.ShowCoreAsync(xamlRoot, canonicalPath, current, cancellationToken);
    }

    private async Task<MapArtworkRegistration?> ShowCoreAsync(
        XamlRoot xamlRoot, string canonicalPath, MapArtworkRegistration? current,
        CancellationToken cancellationToken)
    {
        _canonicalPath = canonicalPath;
        BitmapImage sourceBitmap;
        BitmapImage referenceBitmap;
        try
        {
            var source = await Task.Run(() => ReadImage(_artworkPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var reference = await Task.Run(() => ReadImage(canonicalPath), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            (_sourceWidth, _sourceHeight) = (source.Width, source.Height);
            (_referenceWidth, _referenceHeight) = (reference.Width, reference.Height);
            sourceBitmap = await CreateBitmapAsync(source.Bytes);
            cancellationToken.ThrowIfCancellationRequested();
            referenceBitmap = await CreateBitmapAsync(reference.Bytes);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            await ShowCancelableAsync(new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "无法打开配准图片",
                Content = new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "关闭"
            }, cancellationToken);
            return null;
        }

        var restored = false;
        if (current is not null)
        {
            try
            {
                current.Validate();
                if (current.SourceWidth == _sourceWidth && current.SourceHeight == _sourceHeight
                    && (_sourceCropRegion is null && _sourceCropPoints.Count == 0
                        || current.SourceCropRegion is not null || current.SourceCropPoints.Count > 0))
                {
                    _sourceCropRegion = current.SourceCropRegion?.Clone();
                    _sourceCropPoints = current.SourceCropPoints.Select(point => point.Clone()).ToList();
                }
                if (current.SourceWidth == _sourceWidth && current.SourceHeight == _sourceHeight
                    && current.ReferenceWidth == _referenceWidth && current.ReferenceHeight == _referenceHeight)
                {
                    _points.AddRange(current.Landmarks.Select(point => point.Clone()));
                    _fitMethod.SelectedIndex = (int)current.FitMethod;
                    restored = true;
                }
            }
            catch (Exception)
            {
                // Invalid prior input is not applied to the new image pair.
            }
        }

        var width = Math.Max(240d, Math.Min(920d, xamlRoot.Size.Width - 112d));
        var imageHeight = Math.Clamp(xamlRoot.Size.Height * 0.3, 140d, 300d);
        ConfigureSurface(_sourceSurface, _sourceDots, sourceBitmap, imageHeight);
        ConfigureSurface(_referenceSurface, _referenceDots, referenceBitmap, imageHeight);
        _sourceSurface.PointerPressed += (_, args) => AddPoint(args, source: true);
        _referenceSurface.PointerPressed += (_, args) => AddPoint(args, source: false);
        _sourceSurface.SizeChanged += (_, _) => RenderDots();
        _referenceSurface.SizeChanged += (_, _) => RenderDots();

        var images = new Grid { ColumnSpacing = 12 };
        images.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        images.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        images.Children.Add(Labeled("1 · 作者原图", _sourceSurface));
        var referencePanel = Labeled("2 · 本层结构底图", _referenceSurface);
        Grid.SetColumn(referencePanel, 1);
        images.Children.Add(referencePanel);

        _undoButton.Click += (_, _) =>
        {
            if (_pendingSource is not null) _pendingSource = null;
            else if (_points.Count > 0) _points.RemoveAt(_points.Count - 1);
            PointsChanged();
        };
        _resetButton.Click += (_, _) =>
        {
            _pendingSource = null;
            _points.Clear();
            PointsChanged();
        };
        _fitButton.Click += async (_, _) => await FitPreviewAsync();
        _fitMethod.SelectionChanged += (_, _) => PointsChanged();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(_undoButton);
        actions.Children.Add(_resetButton);
        actions.Children.Add(_fitButton);

        var preview = CreateSurface();
        preview.Height = imageHeight;
        preview.Children.Add(new Image { Source = referenceBitmap, Stretch = Stretch.Uniform });
        preview.Children.Add(_previewArtwork);
        var opacity = new Slider { Minimum = 0, Maximum = 100, Value = 50, Header = "预览小抄透明度" };
        opacity.ValueChanged += (_, args) => _previewArtwork.Opacity = args.NewValue / 100d;
        var content = new StackPanel { Spacing = 10, Width = width };
        content.Children.Add(new TextBlock
        {
            Text = "先点左图，再点右图的同一位置。至少配对三处分散且不共线的房间拐角；"
                + "建议覆盖整层，并用未选作对应点的远端房间检查叠加。每层需要独立配准。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(images);
        content.Children.Add(CreateSourceCropControls());
        content.Children.Add(_status);
        content.Children.Add(_fitMethod);
        content.Children.Add(actions);
        content.Children.Add(_residual);
        content.Children.Add(Labeled("叠加预览 · 结构底图 + 配准小抄", preview));
        content.Children.Add(opacity);
        content.Children.Add(new TextBlock
        {
            Text = "这是本层小抄的整图对齐。请检查未参与配对的房间；对齐不会改变识别底图的结构。",
            TextWrapping = TextWrapping.Wrap
        });
        _dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "对准本层结构底图",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = Math.Max(180d, xamlRoot.Size.Height - 220d),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            },
            PrimaryButtonText = "保存此配准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        _dialog.Resources["ContentDialogMaxWidth"] = width + 64d;
        _dialog.Resources["ContentDialogMaxHeight"] = Math.Max(300d, xamlRoot.Size.Height - 32d);
        PointsChanged();
        if (current is not null)
            _residual.Text = restored
                ? "已恢复对应点，请重新计算并检查预览后保存。"
                : "图片尺寸或已有配准不匹配，请重新选择对应点。";
        try
        {
            var result = await ShowCancelableAsync(_dialog, cancellationToken);
            return result == ContentDialogResult.Primary ? _fitted?.Clone() : null;
        }
        finally
        {
            _closed = true;
        }
    }

    private static async Task<ContentDialogResult> ShowCancelableAsync(
        ContentDialog dialog, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ContentDialogResult.None;
        using var registration = cancellationToken.Register(() =>
            dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        if (cancellationToken.IsCancellationRequested) return ContentDialogResult.None;
        var result = await dialog.ShowThemedAsync();
        return cancellationToken.IsCancellationRequested ? ContentDialogResult.None : result;
    }

    private void AddPoint(PointerRoutedEventArgs args, bool source)
    {
        if (_selectingSourceCrop && !source)
        {
            _status.Text = "请先在左侧作者原图点出本层范围的两个对角。";
            return;
        }
        var surface = source ? _sourceSurface : _referenceSurface;
        var pointer = args.GetCurrentPoint(surface);
        if (!pointer.Properties.IsLeftButtonPressed) return;
        if (source != (_pendingSource is null))
        {
            _status.Text = _pendingSource is null ? "请先在左侧作者原图选择位置。" : "请在右侧结构底图选择对应位置。";
            return;
        }
        var width = source ? _sourceWidth : _referenceWidth;
        var height = source ? _sourceHeight : _referenceHeight;
        var bounds = ImageBounds(surface, width, height);
        var position = pointer.Position;
        if (bounds.Width <= 0 || bounds.Height <= 0 || position.X < bounds.X || position.Y < bounds.Y
            || position.X >= bounds.Right || position.Y >= bounds.Bottom) return;
        var pixel = new Point((position.X - bounds.X) * width / bounds.Width,
            (position.Y - bounds.Y) * height / bounds.Height);
        if (_selectingSourceCrop)
        {
            SelectSourceCropCorner(pixel);
            return;
        }
        if (source) _pendingSource = pixel;
        else
        {
            var start = _pendingSource!.Value;
            _points.Add(new MapArtworkLandmark
            {
                SourceX = start.X, SourceY = start.Y, ReferenceX = pixel.X, ReferenceY = pixel.Y
            });
            _pendingSource = null;
        }
        PointsChanged();
        args.Handled = true;
    }

    private void PointsChanged()
    {
        _editVersion++;
        _fitted = null;
        _previewArtwork.Source = null;
        _residual.Text = "请计算预览；保存前检查整层叠加效果。";
        if (_dialog is not null) _dialog.IsPrimaryButtonEnabled = false;
        _status.Text = _pendingSource is null
            ? $"已配对 {_points.Count} 组 · 请在左图选择第 {_points.Count + 1} 个位置。"
            : $"已配对 {_points.Count} 组 · 请在右图选择第 {_points.Count + 1} 个对应位置。";
        UpdateButtons();
        RenderDots();
    }

    private void UpdateButtons()
    {
        _undoButton.IsEnabled = _resetButton.IsEnabled = _points.Count > 0 || _pendingSource is not null;
        _fitButton.IsEnabled = !_busy && !_selectingSourceCrop && _points.Count >= 3 && _pendingSource is null;
        _fitMethod.IsEnabled = !_busy;
        _cropButton.IsEnabled = _wholeArtworkButton.IsEnabled = !_busy;
    }

    private async Task FitPreviewAsync()
    {
        if (_busy || _selectingSourceCrop || _pendingSource is not null || _points.Count < 3) return;
        var version = _editVersion;
        var points = _points.Select(point => point.Clone()).ToArray();
        var fitMethod = (MapArtworkFitMethod)_fitMethod.SelectedIndex;
        var cropRegion = _sourceCropRegion?.Clone();
        var cropPoints = _sourceCropPoints.Select(point => point.Clone()).ToList();
        _busy = true;
        _fitted = null;
        _dialog!.IsPrimaryButtonEnabled = false;
        _residual.Text = "正在计算叠加预览…";
        UpdateButtons();
        try
        {
            var result = await Task.Run(() =>
            {
                var registration = MapArtworkRegistrationService.Fit(
                    _sourceWidth, _sourceHeight, _referenceWidth, _referenceHeight, points, fitMethod);
                registration.SourceCropRegion = cropRegion;
                registration.SourceCropPoints = cropPoints;
                registration.SourceCropConfigured = true;
                registration.Validate();
                using var source = Cv2.ImDecode(File.ReadAllBytes(_artworkPath), ImreadModes.Unchanged);
                using var canonical = Cv2.ImDecode(File.ReadAllBytes(_canonicalPath), ImreadModes.Unchanged);
                using var baked = MapArtworkRegistrationService.BakeOverlay(source, canonical, registration);
                if (!Cv2.ImEncode(".png", baked, out var bytes))
                    throw new InvalidOperationException("无法创建叠加预览。");
                return (Registration: registration, Bytes: bytes,
                    Residuals: MapArtworkRegistrationService.EvaluateResiduals(registration, points));
            });
            if (_closed || version != _editVersion) return;
            var bitmap = await CreateBitmapAsync(result.Bytes);
            if (_closed || version != _editVersion) return;
            _previewArtwork.Source = bitmap;
            _fitted = result.Registration;
            _residual.Text = $"拟合用点：{points.Length} 组；平均误差 {result.Residuals.Average():F2} 像素，"
                + $"最大 {result.Residuals.Max():F2} 像素（结构底图像素）。"
                + "这是训练点误差；三组点可恰好拟合，请另查未参与拟合的房间。";
            _dialog.IsPrimaryButtonEnabled = true;
        }
        catch (Exception exception)
        {
            if (!_closed && version == _editVersion)
                _residual.Text = $"无法生成配准预览：{exception.Message}";
        }
        finally
        {
            _busy = false;
            if (!_closed) UpdateButtons();
        }
    }

    private void RenderDots()
    {
        _sourceDots.Children.Clear();
        _referenceDots.Children.Clear();
        var sourceBounds = ImageBounds(_sourceSurface, _sourceWidth, _sourceHeight);
        var referenceBounds = ImageBounds(_referenceSurface, _referenceWidth, _referenceHeight);
        for (var index = 0; index < _points.Count; index++)
        {
            var point = _points[index];
            AddDot(_sourceDots, sourceBounds, point.SourceX / _sourceWidth, point.SourceY / _sourceHeight, index + 1);
            AddDot(_referenceDots, referenceBounds, point.ReferenceX / _referenceWidth, point.ReferenceY / _referenceHeight, index + 1);
        }
        if (_pendingSource is { } pending)
            AddDot(_sourceDots, sourceBounds, pending.X / _sourceWidth, pending.Y / _sourceHeight, _points.Count + 1);
        RenderSourceCrop(sourceBounds);
    }

    private static void AddDot(Canvas canvas, Rect bounds, double x, double y, int number)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var dot = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(230, 160, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = number.ToString(), FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        Canvas.SetLeft(dot, bounds.X + x * bounds.Width - 12);
        Canvas.SetTop(dot, bounds.Y + y * bounds.Height - 12);
        canvas.Children.Add(dot);
    }

    private static Rect ImageBounds(Grid surface, int width, int height)
    {
        if (width <= 0 || height <= 0 || surface.ActualWidth <= 0 || surface.ActualHeight <= 0)
            return Rect.Empty;
        var scale = Math.Min(surface.ActualWidth / width, surface.ActualHeight / height);
        return new Rect((surface.ActualWidth - width * scale) / 2,
            (surface.ActualHeight - height * scale) / 2, width * scale, height * scale);
    }

    private static Grid CreateSurface() => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(255, 24, 28, 34))
    };

    private static void ConfigureSurface(Grid surface, Canvas dots, BitmapImage bitmap, double height)
    {
        surface.Height = height;
        surface.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        surface.Children.Add(dots);
    }

    private static StackPanel Labeled(string label, UIElement element)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(element);
        return panel;
    }

    private static (byte[] Bytes, int Width, int Height) ReadImage(string path)
    {
        using var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Unchanged);
        if (image.Empty()) throw new InvalidOperationException("图片无法读取，请检查文件是否存在且格式受支持。");
        if (image.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("请选择 8 位图片用于配准。");
        if (!Cv2.ImEncode(".png", image, out var bytes))
            throw new InvalidOperationException("无法解码图片预览。");
        return (bytes, image.Width, image.Height);
    }

    private static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes)
    {
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
