using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor : UserControl
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

    public SurveyProjectEditor(ISurveyCoordinator coordinator, Guid projectId)
    {
        _session = new SurveyEditorSession(coordinator, projectId);
        _layers = new SurveyLayerPanel(_session);
        Content = BuildLayout();
        _session.SnapshotChanged += Session_SnapshotChanged;
        _session.Error += (_, message) => SetStatus(message, isError: true);
        _layers.SelectionChanged += (_, args) =>
            _canvas.SelectLayers(args.LayerIds, args.PrimaryLayerId);
        _canvas.LayerSelected += (_, layerId) => _layers.SelectLayer(layerId);
        _canvas.LayerToolInvoked += Canvas_LayerToolInvoked;
        _canvas.MaskStrokeCommitted += Canvas_MaskStrokeCommitted;
        _canvas.TransformCommitted += Canvas_TransformCommitted;
        _canvas.ZoomChanged += Canvas_ZoomChanged;
        Loaded += OnLoaded;
    }

    public event EventHandler? CloseRequested;

    private FrameworkElement BuildLayout()
        => BuildEditorLayout();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
            return;
        _loaded = true;
        SetStatus("正在加载测绘项目……");
        try
        {
            await _session.LoadAsync();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void Session_SnapshotChanged(object? sender, EventArgs e)
    {
        if (_session.Snapshot is not { } snapshot)
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
            await _canvas.RenderAsync(_session, _floorKey);
        }
        catch (Exception exception)
        {
            SetStatus($"图层图像加载失败：{exception.Message}", isError: true);
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
        if (_floorPicker.SelectedItem is not ComboBoxItem { Tag: string floorKey }
            || string.Equals(floorKey, _floorKey, StringComparison.OrdinalIgnoreCase))
            return;
        _floorKey = floorKey;
        _layers.SetFloor(floorKey);
        try
        {
            await _canvas.RenderAsync(_session, floorKey);
            _canvas.FitAfterNextLayout();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }

    private async void Canvas_TransformCommitted(object? sender, SurveyLayerTransformEventArgs e)
    {
        await _session.EditAsync(
            e.LayerId,
            state => state with { ManualTransform = e.Transform });
    }

    private async void Canvas_LayerToolInvoked(object? sender, SurveyLayerToolEventArgs e)
    {
        if (!_layers.SelectedLayerIds.Contains(e.LayerId))
        {
            SetStatus("请先在右侧图层面板中选择目标图层。", isError: true);
            return;
        }
        if (e.Tool == SurveyEditorTool.Decontaminate)
        {
            SetStatus("正在处理图层去污状态…");
            await _session.ToggleDecontaminationAsync(e.LayerId);
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
            e.LayerId);
        if (result is null)
            return;
        var succeeded = result.Items.Count(item => item.Succeeded);
        var failed = result.Items.Where(item => !item.Succeeded).ToArray();
        SetStatus(failed.Length == 0
            ? $"魔术贴已对齐 {succeeded} 个图层。"
            : $"魔术贴已对齐 {succeeded} 个图层，{failed.Length} 个未匹配："
                + string.Join("；", failed.Select(item => item.Message)));
    }

    private async void Canvas_MaskStrokeCommitted(object? sender, SurveyMaskStrokeEventArgs e)
    {
        if (_session.Snapshot is not { } snapshot)
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
        var result = await _session.ApplyMaskStrokeAsync(
            floor.FloorId,
            targets,
            e.Points,
            _brushSize,
            _brushShape);
        if (result is null)
            return;
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

    private void SetStatus(string message, bool isError = false)
    {
        _status.Text = message;
        _status.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 255, 125, 104)
            : Color.FromArgb(255, 151, 166, 187));
    }
}
