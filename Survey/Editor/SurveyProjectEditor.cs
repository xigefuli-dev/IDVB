using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor : UserControl, IDisposable
{
    private readonly SurveyEditorSession _session;
    private readonly SurveyCanvasView _canvas = new();
    private readonly SurveyLayerPanel _layers;
    private readonly TextBlock _title = new() { FontSize = 18 };
    private readonly TextBlock _status = new() { FontSize = 12 };
    private readonly ComboBox _floorPicker = new() { MinWidth = 110 };
    private readonly Button _undoButton = new() { Content = "撤销" };
    private readonly Button _redoButton = new() { Content = "重做" };
    private readonly Dictionary<SurveyEditorTool, Button> _toolButtons = [];
    private SurveyEraseMode _eraseMode = SurveyEraseMode.Eraser;
    private SurveyBrushShape _brushShape = SurveyBrushShape.Circle;
    private double _brushSize = 64d;
    private Flyout? _eraserFlyout;
    private NumberBox? _zoomPercent;
    private bool _updatingZoom;
    private string _floorKey = "1f";
    private bool _loaded;
    private CancellationTokenSource? _transformCommitDelay;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _renderCancellation;
    private int _layerToolOperationActive;
    private bool _disposed;

    public SurveyProjectEditor(ISurveyCoordinator coordinator, Guid projectId)
    {
        _session = new SurveyEditorSession(coordinator, projectId);
        _layers = new SurveyLayerPanel(_session);
        Content = BuildLayout();
        _session.SnapshotChanged += Session_SnapshotChanged;
        _session.Error += Session_Error;
        _layers.SelectionChanged += Layers_SelectionChanged;
        _canvas.LayerSelected += Canvas_LayerSelected;
        _canvas.LayerToolInvoked += Canvas_LayerToolInvoked;
        _canvas.MaskStrokeCommitted += Canvas_MaskStrokeCommitted;
        _canvas.TransformCommitted += Canvas_TransformCommitted;
        _canvas.ZoomChanged += Canvas_ZoomChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? CloseRequested;

    private void Session_Error(object? sender, string message) =>
        SetStatus(message, isError: true);

    private void Layers_SelectionChanged(
        object? sender,
        SurveyLayerSelectionEventArgs args) =>
        _canvas.SelectLayers(args.LayerIds, args.PrimaryLayerId);

    private void Canvas_LayerSelected(object? sender, Guid layerId) =>
        _layers.SelectLayer(layerId);

    private FrameworkElement BuildLayout()
        => BuildEditorLayout();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || _disposed)
            return;
        _loaded = true;
        SetStatus("正在加载测绘项目……");
        try
        {
            await _session.LoadAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private async void Session_SnapshotChanged(object? sender, EventArgs e)
    {
        if (_disposed || _session.Snapshot is not { } snapshot)
            return;
        _title.Text = $"{snapshot.Project.Name} · 测绘地图编辑器";
        UpdateFloorPicker(snapshot);
        _layers.SetFloor(_floorKey);
        _undoButton.IsEnabled = _session.CanUndo;
        _redoButton.IsEnabled = _session.CanRedo;
        SetStatus(
            $"修订 {snapshot.Project.Revision} · 自动保存完成 · "
            + $"{snapshot.Layers.Count(item => !item.IsDeleted)} 个活动图层 · "
            + $"{snapshot.Observations.Count(item => item.State == SurveyObservationState.Unregistered)} 个未对齐");
        try
        {
            await RenderCanvasAsync(_floorKey);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"图层图像加载失败：{exception.Message}", isError: true);
        }
    }

    private async Task RenderCanvasAsync(string floorKey, bool fitAfterRender = false)
    {
        if (_disposed)
            return;
        var renderCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var previousRender = _renderCancellation;
        _renderCancellation = renderCancellation;
        previousRender?.Cancel();
        try
        {
            await _canvas.RenderAsync(_session, floorKey, renderCancellation.Token);
            if (fitAfterRender && !renderCancellation.IsCancellationRequested)
                _canvas.FitAfterNextLayout();
        }
        finally
        {
            if (ReferenceEquals(_renderCancellation, renderCancellation))
                _renderCancellation = null;
            renderCancellation.Dispose();
        }
    }

    private void UpdateFloorPicker(SurveyProjectSnapshot snapshot)
    {
        var previous = _floorKey;
        _floorPicker.Items.Clear();
        foreach (var floor in snapshot.Floors.OrderBy(item => item.Order))
        {
            _floorPicker.Items.Add(new ComboBoxItem
            {
                Content = floor.DisplayName,
                Tag = floor.FloorKey
            });
        }
        var selected = _floorPicker.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, previous, StringComparison.OrdinalIgnoreCase))
            ?? _floorPicker.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (selected is not null)
        {
            _floorKey = (string)selected.Tag;
            _floorPicker.SelectedItem = selected;
        }
    }

    private async void FloorPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_disposed
            || _floorPicker.SelectedItem is not ComboBoxItem { Tag: string floorKey }
            || string.Equals(floorKey, _floorKey, StringComparison.OrdinalIgnoreCase))
            return;
        _floorKey = floorKey;
        _layers.SetFloor(floorKey);
        try
        {
            await RenderCanvasAsync(floorKey, fitAfterRender: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void Canvas_TransformCommitted(object? sender, SurveyLayerTransformEventArgs e)
    {
        if (_disposed)
            return;
        _transformCommitDelay?.Cancel();
        _transformCommitDelay?.Dispose();
        var delay = _transformCommitDelay = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        try
        {
            // Coalesce pointer movement and key-repeat into one persisted edit.
            await Task.Delay(120, delay.Token);
            await _session.EditAsync(
                e.LayerId,
                state => state with { ManualTransform = e.Transform },
                delay.Token);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested)
        {
        }
    }

    private async void Canvas_LayerToolInvoked(object? sender, SurveyLayerToolEventArgs e)
    {
        if (_disposed)
            return;
        if (Interlocked.CompareExchange(ref _layerToolOperationActive, 1, 0) != 0)
        {
            SetStatus("上一个图层处理仍在进行，请稍候。", isError: true);
            return;
        }
        SetLayerToolButtonsEnabled(false);
        try
        {
        if (!_layers.SelectedLayerIds.Contains(e.LayerId))
        {
            SetStatus("请先在右侧图层面板中选择目标图层。", isError: true);
            return;
        }
        if (e.Tool == SurveyEditorTool.Decontaminate)
        {
            SetStatus("正在处理图层去污状态…");
            await _session.ToggleDecontaminationAsync(
                e.LayerId,
                _lifetimeCancellation.Token);
            return;
        }
        if (e.Tool == SurveyEditorTool.NormalizeColors)
        {
            if (_layers.SelectedLayerIds.Count < 2)
            {
                SetStatus("融色至少需要选择两个同楼层图层。", isError: true);
                return;
            }
            SetStatus("正在以点击的图层为基准统一颜色…");
            var normalized = await _session.NormalizeLayerColorsAsync(
                _layers.SelectedLayerIds.ToArray(),
                e.LayerId,
                _lifetimeCancellation.Token);
            if (normalized is null)
                return;
            var normalizedCount = normalized.Items.Count(item => item.Succeeded);
            var normalizationFailures = normalized.Items.Count(item => !item.Succeeded);
            SetStatus(normalizationFailures == 0
                ? $"已完成 {normalizedCount} 个图层的颜色归一化。"
                : $"已完成 {normalizedCount} 个图层的颜色归一化，{normalizationFailures} 个未处理。");
            return;
        }
        if (e.Tool != SurveyEditorTool.Align)
            return;
        if (_layers.SelectedLayerIds.Count < 2)
        {
            SetStatus("魔术贴至少需要选择两个同楼层图层。", isError: true);
            return;
        }
        SetStatus("正在以点击的图层为基准执行魔术贴对齐…");
        var result = await _session.AlignLayersAsync(
            _layers.SelectedLayerIds.ToArray(),
            e.LayerId,
            _lifetimeCancellation.Token);
        if (result is null)
            return;
        var succeeded = result.Items.Count(item => item.Succeeded);
        var failed = result.Items.Where(item => !item.Succeeded).ToArray();
        SetStatus(failed.Length == 0
            ? $"魔术贴已对齐 {succeeded} 个图层。"
            : $"魔术贴已对齐 {succeeded} 个图层，{failed.Length} 个未匹配："
                + string.Join("；", failed.Select(item => item.Message)));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
                SetStatus($"图层处理失败：{exception.Message}", isError: true);
        }
        finally
        {
            Interlocked.Exchange(ref _layerToolOperationActive, 0);
            if (!_disposed)
                SetLayerToolButtonsEnabled(true);
        }
    }

    private void SetLayerToolButtonsEnabled(bool enabled)
    {
        foreach (var button in _toolButtons.Values)
            button.IsEnabled = enabled;
    }

    private async void Canvas_MaskStrokeCommitted(object? sender, SurveyMaskStrokeEventArgs e)
    {
        if (_disposed || _session.Snapshot is not { } snapshot)
            return;
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase));
        if (floor is null)
            return;
        Guid[] targets;
        if (_eraseMode == SurveyEraseMode.Eraser)
        {
            if (_layers.PrimaryLayerId is not { } primary)
            {
                SetStatus("橡皮擦需要一个主选图层。", isError: true);
                return;
            }
            targets = [primary];
        }
        else
        {
            targets = snapshot.Layers
                .Where(item => item.FloorId == floor.FloorId
                    && !item.IsDeleted && item.IsVisible && !item.IsLocked)
                .Select(item => item.LayerId)
                .ToArray();
        }
        SurveyLayerOperationResult? result;
        try
        {
            result = await _session.ApplyMaskStrokeAsync(
                floor.FloorId,
                targets,
                e.Points,
                _brushSize,
                _brushShape,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        if (result is null)
        {
            _canvas.ClearMaskPreview();
            return;
        }
        var affected = result.Items.Count(item => item.Succeeded);
        SetStatus(affected == 0
            ? "笔划没有与可编辑图层相交。"
            : $"{(_eraseMode == SurveyEraseMode.Eraser ? "橡皮擦" : "砂纸")}已隐藏 {affected} 个图层中的指定区域。");
    }

    private void Canvas_ZoomChanged(object? sender, SurveyZoomChangedEventArgs e)
    {
        if (_zoomPercent is null)
            return;
        _updatingZoom = true;
        _zoomPercent.Value = Math.Round(e.Percent);
        _updatingZoom = false;
    }

    private async Task ShowProjectPropertiesAsync()
    {
        if (_session.Snapshot is not { } snapshot)
            return;
        var name = new TextBox { Header = "项目名称", Text = snapshot.Project.Name };
        var mapClass = new TextBox { Header = "Class", Text = snapshot.Project.MapClass };
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase));
        var floorName = new TextBox
        {
            Header = "当前楼层名称",
            Text = floor?.DisplayName ?? _floorKey
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(name);
        content.Children.Add(mapClass);
        content.Children.Add(floorName);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "测绘项目属性",
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await _session.UpdateMetadataAsync(
            name.Text,
            mapClass.Text,
            floor?.FloorId,
            floorName.Text);
    }

    private async Task ExportCurrentFloorPngAsync()
    {
        if (_session.Snapshot is not { } snapshot)
            return;
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = CreatePngSuggestedFileName(snapshot.Project.Name, _floorKey),
                DefaultFileExtension = ".png",
                CommitButtonText = "导出",
                FileTypeChoices = { { "PNG 图片", new List<string> { ".png" } } }
            };
            var file = await picker.PickSaveFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                return;
            SetStatus("正在导出当前楼层 PNG…");
            var result = await _session.RenderOutputsAsync(_floorKey);
            if (!result.Succeeded || result.Value is null)
            {
                SetStatus(result.Message ?? "PNG 导出失败。", isError: true);
                return;
            }
            await using var source = await _session.OpenAssetAsync(result.Value.VisualMap.Asset);
            await using var destination = new FileStream(
                file.Path, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination);
            SetStatus($"PNG 已导出：{file.Path}");
        }
        catch (Exception exception)
        {
            SetStatus($"PNG 导出失败：{exception.Message}", isError: true);
        }
    }

    private static string CreatePngSuggestedFileName(string projectName, string floorKey)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var raw = $"{projectName}-{floorKey}";
        var sanitized = new string(raw
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        while (sanitized.Contains("__", StringComparison.Ordinal))
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "Identity-Vision-Bridge-Map";
        if (IsReservedWindowsFileName(sanitized))
            sanitized = $"IDVB-{sanitized}";
        return sanitized.Length <= 120 ? sanitized : sanitized[..120].TrimEnd('.', ' ');
    }

    private static bool IsReservedWindowsFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }

    private void SetStatus(string message, bool isError = false)
    {
        if (_disposed)
            return;
        _status.Text = message;
        _status.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 255, 125, 104)
            : Color.FromArgb(255, 151, 166, 187));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _lifetimeCancellation.Cancel();
        _renderCancellation?.Cancel();
        _renderCancellation = null;
        _transformCommitDelay?.Cancel();
        _transformCommitDelay?.Dispose();
        _transformCommitDelay = null;
        _session.SnapshotChanged -= Session_SnapshotChanged;
        _session.Error -= Session_Error;
        _layers.SelectionChanged -= Layers_SelectionChanged;
        _canvas.LayerSelected -= Canvas_LayerSelected;
        _canvas.LayerToolInvoked -= Canvas_LayerToolInvoked;
        _canvas.MaskStrokeCommitted -= Canvas_MaskStrokeCommitted;
        _canvas.TransformCommitted -= Canvas_TransformCommitted;
        _canvas.ZoomChanged -= Canvas_ZoomChanged;
        _floorPicker.SelectionChanged -= FloorPicker_SelectionChanged;
        _layers.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        foreach (var button in _toolButtons.Values)
            button.IsEnabled = false;
        _toolButtons.Clear();
        CloseRequested = null;
        Content = null;
        _lifetimeCancellation.Dispose();
    }
}
