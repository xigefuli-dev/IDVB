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

    private async Task ImportImageAsync()
    {
        if (_session.Snapshot is not { } snapshot)
            return;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "选择",
                ViewMode = PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            var picked = await picker.PickSingleFileAsync();
            if (picked is null || string.IsNullOrWhiteSpace(picked.Path))
                return;
            var file = await StorageFile.GetFileFromPathAsync(picked.Path);

            byte[] bytes;
            int width, height;
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                // 服务端不解码图片，这里用 BitmapDecoder 读取真实像素尺寸。
                var decoder = await BitmapDecoder.CreateAsync(stream);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
                stream.Seek(0);
                using var memory = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            if (bytes.Length == 0)
            {
                SetStatus("选中的图片为空。", isError: true);
                return;
            }
            if (width <= 0 || height <= 0)
            {
                SetStatus("无法识别该图片的尺寸。", isError: true);
                return;
            }

            var extension = file.FileType.ToLowerInvariant();
            var mediaType = extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };

            var floor = await PickImportFloorAsync(snapshot, _floorKey);
            if (floor is null)
                return;

            SetStatus("正在导入图片…");
            var result = await _session.ImportObservationAsync(
                bytes,
                extension,
                mediaType,
                width,
                height,
                floor.FloorKey,
                Path.GetFileName(file.Path),
                _lifetimeCancellation.Token);
            if (result is null)
                return;
            SetStatus(result.WasAlreadyCommitted
                ? "该图片已经导入到当前楼层，未创建重复图层。"
                : $"已导入“{result.Layer.Name}”。");

            // 导入到当前楼层时画布由图层面板刷新自动更新；跨楼层需手动切换。
            if (!string.Equals(floor.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase))
            {
                _floorKey = floor.FloorKey;
                _floorPicker.SelectedItem = _floorPicker.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item =>
                        string.Equals(item.Tag as string, _floorKey, StringComparison.OrdinalIgnoreCase));
                _layers.SetFloor(_floorKey);
                await RenderCanvasAsync(_floorKey, fitAfterRender: true);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"图片导入失败：{exception.Message}", isError: true);
        }
    }

    private async Task<SurveyFloor?> PickImportFloorAsync(
        SurveyProjectSnapshot snapshot,
        string currentFloorKey)
    {
        var combo = new ComboBox
        {
            Header = "目标楼层",
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = snapshot.Floors.OrderBy(floor => floor.Order)
                .Select(floor => new ComboBoxItem
                {
                    Content = $"{floor.DisplayName}（{floor.FloorKey}）",
                    Tag = floor.FloorKey
                })
                .ToList()
        };
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Tag as string, currentFloorKey, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入本地图片",
            Content = new StackPanel { Spacing = 8, Children = { combo } },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return combo.SelectedItem is ComboBoxItem { Tag: string floorKey }
            ? snapshot.Floors.FirstOrDefault(floor =>
                string.Equals(floor.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase))
            : null;
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
        _layers.IsolationChanged -= Layers_IsolationChanged;
        _canvas.LayerSelected -= Canvas_LayerSelected;
        _canvas.LayerToolInvoked -= Canvas_LayerToolInvoked;
        _canvas.LayerPixelSampleRequested -= Canvas_LayerPixelSampleRequested;
        _canvas.MaskStrokeCommitted -= Canvas_MaskStrokeCommitted;
        _canvas.TransformCommitted -= Canvas_TransformCommitted;
        _canvas.ZoomChanged -= Canvas_ZoomChanged;
        DisposeKeyboardNavigation();
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
