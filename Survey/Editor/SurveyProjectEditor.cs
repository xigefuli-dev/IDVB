using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor : UserControl, IDisposable
{
    private readonly SurveyEditorSession _session;
    private readonly ISurveyTemplateStore _templateStore;
    private readonly SurveyCanvasView _canvas = new();
    private readonly SurveyLayerPanel _layers;
    private readonly TextBlock _title = new() { FontSize = 18 };
    private readonly TextBlock _status = new() { FontSize = 12 };
    private readonly ComboBox _floorPicker = new() { MinWidth = 110 };
    private readonly Button _undoButton = new() { Content = "撤销" };
    private readonly Button _redoButton = new() { Content = "重做" };
    private readonly Dictionary<SurveyEditorTool, Button> _toolButtons = [];
    private readonly List<SurveyColorTemplate> _templates = [];
    private readonly List<SurveyColorTemplateEntry> _draftTemplateEntries = [];
    private SurveyEraseMode _eraseMode = SurveyEraseMode.Eraser;
    private SurveyBrushShape _brushShape = SurveyBrushShape.Circle;
    private double _brushSize = 64d;
    private SurveyColor _paintColor = new(220, 60, 45);
    private byte _fillTolerance = 24;
    private Flyout? _paintFlyout;
    private Flyout? _eraserFlyout;
    private Flyout? _vignetteFlyout;
    private Flyout? _templateFlyout;
    private SurveyTemplateMode _templateMode = SurveyTemplateMode.Create;
    private ComboBox? _templateModePicker;
    private ComboBox? _templateColorTypePicker;
    private ComboBox? _templatePicker;
    private TextBox? _templateNameBox;
    private StackPanel? _templateDraftList;
    private Border? _templateSamplePreview;
    private TextBlock? _templateSampleText;
    private Button? _templateSaveButton;
    private Button? _templateCancelEditButton;
    private Button? _templateSamplerButton;
    private Guid? _editingTemplateId;
    private bool _templateSaveInProgress;
    private double _vignetteStart = 0.5d;
    private double _vignetteStrength = 0.5d;
    private NumberBox? _zoomPercent;
    private bool _updatingZoom;
    private string _floorKey = "1f";
    private bool _loaded;
    private CancellationTokenSource? _transformCommitDelay;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private CancellationTokenSource? _renderCancellation;
    private int _layerToolOperationActive;
    private bool _disposed;

    public SurveyProjectEditor(
        ISurveyCoordinator coordinator,
        Guid projectId,
        ISurveyTemplateStore templateStore)
    {
        _session = new SurveyEditorSession(coordinator, projectId);
        _templateStore = templateStore;
        _layers = new SurveyLayerPanel(_session);
        Content = BuildLayout();
        _session.SnapshotChanged += Session_SnapshotChanged;
        _session.Error += Session_Error;
        _layers.SelectionChanged += Layers_SelectionChanged;
        _layers.IsolationChanged += Layers_IsolationChanged;
        _canvas.LayerSelected += Canvas_LayerSelected;
        _canvas.LayerToolInvoked += Canvas_LayerToolInvoked;
        _canvas.LayerPixelSampleRequested += Canvas_LayerPixelSampleRequested;
        _canvas.MaskStrokeCommitted += Canvas_MaskStrokeCommitted;
        _canvas.ColorStrokeCommitted += Canvas_ColorStrokeCommitted;
        _canvas.ColorFillRequested += Canvas_ColorFillRequested;
        _canvas.TransformCommitted += Canvas_TransformCommitted;
        _canvas.ZoomChanged += Canvas_ZoomChanged;
        InitializeKeyboardNavigation();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? CloseRequested;

    private void Session_Error(object? sender, string message) =>
        SetStatus(message, isError: true);

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
            var templates = await _templateStore.LoadAsync(_lifetimeCancellation.Token);
            _templates.Clear();
            _templates.AddRange(templates);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, isError: true);
        }
    }
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
        if (e.Tool == SurveyEditorTool.Template && _templateMode == SurveyTemplateMode.Apply)
        {
            var template = _templatePicker?.SelectedItem as SurveyColorTemplate;
            if (template is null || template.Entries.Count == 0)
            {
                SetStatus("请先选择一个至少包含一种颜色的模板。", isError: true);
                return;
            }
            var targetLayerIds = _layers.SelectedLayerIds.Count > 1
                ? _layers.SelectedLayerIds.ToArray()
                : [e.LayerId];
            SetStatus(targetLayerIds.Length > 1
                ? $"正在将模板“{template.Name}”套用到 {targetLayerIds.Length} 个图层…"
                : $"正在将模板“{template.Name}”套用到图层…");
            var applied = await _session.ApplyColorTemplateAsync(
                targetLayerIds,
                template.Entries,
                _lifetimeCancellation.Token);
            if (applied is null)
                return;
            var templateSucceeded = applied.Items.Count(item => item.Succeeded);
            var templateFailed = applied.Items.Count - templateSucceeded;
            SetStatus(templateFailed == 0
                ? $"模板“{template.Name}”已套用到 {templateSucceeded} 个图层。"
                : $"模板“{template.Name}”已处理 {templateSucceeded} 个图层，{templateFailed} 个未处理。",
                isError: templateSucceeded == 0);
            return;
        }
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
        if (e.Tool == SurveyEditorTool.VignetteCorrection)
        {
            SetStatus("正在应用晕影校正…");
            var corrected = await _session.CorrectVignetteAsync(
                [e.LayerId],
                _vignetteStart,
                _vignetteStrength,
                _lifetimeCancellation.Token);
            if (corrected is not null)
                SetVignetteResultStatus(corrected);
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

    private async Task ApplyVignetteCorrectionToSelectionAsync()
    {
        if (_layers.SelectedLayerIds.Count == 0)
        {
            SetStatus("请先选择至少一个图层。", isError: true);
            return;
        }
        if (Interlocked.CompareExchange(ref _layerToolOperationActive, 1, 0) != 0)
        {
            SetStatus("上一个图层处理仍在进行，请稍候。", isError: true);
            return;
        }
        SetLayerToolButtonsEnabled(false);
        try
        {
            SetStatus("正在对选中图层应用晕影校正…");
            var result = await _session.CorrectVignetteAsync(
                _layers.SelectedLayerIds.ToArray(),
                _vignetteStart,
                _vignetteStrength,
                _lifetimeCancellation.Token);
            if (result is not null)
                SetVignetteResultStatus(result);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
                SetStatus($"晕影校正失败：{exception.Message}", isError: true);
        }
        finally
        {
            Interlocked.Exchange(ref _layerToolOperationActive, 0);
            if (!_disposed)
                SetLayerToolButtonsEnabled(true);
        }
    }

    private void SetVignetteResultStatus(SurveyLayerOperationResult result)
    {
        var succeeded = result.Items.Count(item => item.Succeeded);
        var failed = result.Items.Count - succeeded;
        SetStatus(failed == 0
            ? $"已对 {succeeded} 个图层应用晕影校正。"
            : $"已校正 {succeeded} 个图层，{failed} 个未处理。",
            isError: succeeded == 0);
    }
    private async void Canvas_LayerPixelSampleRequested(
        object? sender,
        SurveyLayerPixelSampleEventArgs e)
    {
        if (_disposed)
            return;
        if (_canvas.ActiveTool == SurveyEditorTool.Eyedropper)
        {
            await SamplePaintColorAsync(e);
            return;
        }
        if (_templateMode != SurveyTemplateMode.Create)
            return;
        try
        {
            var sampled = await _session.SampleCompositedPixelAsync(
                _floorKey,
                e.WorldPoint,
                _lifetimeCancellation.Token);
            if (sampled is null)
            {
                SetStatus("无法读取当前画面像素。", isError: true);
                return;
            }

            var type = SelectedTemplateColorType();
            var entry = new SurveyColorTemplateEntry(sampled.R, sampled.G, sampled.B, type);
            if (_draftTemplateEntries.Contains(entry))
            {
                SetStatus("该颜色和颜色类型已经记录在当前模板中。", isError: true);
                return;
            }
            _draftTemplateEntries.Add(entry);
            RefreshTemplateDraftList();
            SetTemplateSamplePreview(sampled.R, sampled.G, sampled.B);
            SetStatus($"已记录 {ToTemplateHex(sampled.R, sampled.G, sampled.B)} [{TemplateColorTypeName(type)}]。", false);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"取色失败：{exception.Message}", isError: true);
        }
    }
}
