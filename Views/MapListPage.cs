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

/// <summary>
/// The map-management workflow shown under 加页手记 / 地图列表.
/// </summary>
public sealed class MapListPage : UserControl
{
    private static readonly TimeSpan WorkflowEnterDuration = TimeSpan.FromMilliseconds(200);

    private enum BatchOperationType { None, Import, Edit, Delete }

    private static readonly Color AccentBlue = Color.FromArgb(255, 46, 132, 225);
    private static readonly Color MainEntranceBlue = Color.FromArgb(255, 38, 133, 255);
    private static readonly Color SideEntranceGreen = Color.FromArgb(255, 63, 207, 123);
    private static readonly Color SecondFloorPurple = Color.FromArgb(255, 132, 94, 247);
    private static readonly Color OptionalAnchorOrange = Color.FromArgb(255, 236, 150, 61);
    private static readonly Color RecognitionRegionOrange = Color.FromArgb(255, 239, 103, 42);
    private static readonly Color RecognitionRegionRed = Color.FromArgb(255, 235, 55, 55);
    private static readonly Color DeleteRed = Color.FromArgb(255, 222, 45, 50);
    private static readonly Color DisabledGray = Color.FromArgb(255, 210, 210, 210);
    private static readonly Color[] AnnotationColors =
    [
        Color.FromArgb(255, 255, 59, 48),   // 0: 红
        Color.FromArgb(255, 255, 149, 0),   // 1: 橙
        Color.FromArgb(255, 255, 204, 0),   // 2: 黄
        Color.FromArgb(255, 52, 199, 89),   // 3: 绿
        Color.FromArgb(255, 50, 173, 230),  // 4: 青
        Color.FromArgb(255, 0, 122, 255),   // 5: 蓝
        Color.FromArgb(255, 175, 82, 222),  // 6: 紫
        Color.FromArgb(255, 255, 45, 85),   // 7: 粉
        Color.FromArgb(255, 242, 242, 242), // 8: 白
    ];
    private const double MarkerPanelInset = 12d;
    private const double MarkerPanelTopSafeInset = 48d;
    private const double MarkerPanelPreferredWidth = 170d;

    private readonly MapRepository _repository = new();
    private readonly IdvmPackageService _idvmPackageService;
    private readonly ContentPresenter _workflowHost = new();
    private readonly Dictionary<Guid, Border> _cardBorders = [];
    private readonly Dictionary<string, BitmapImage> _previewImages =
        new(StringComparer.OrdinalIgnoreCase);
    private Button? _editButton;
    private Button? _deleteButton;
    private HashSet<Guid> _selectedMapIds = [];
    private Guid? _lastClickedMapId;
    private IReadOnlyList<MapRecord> _loadedMaps = [];
    private IReadOnlyList<string> _classes = ["S1"];
    private string _selectedClass = "S1";
    private bool _isClassDeleteMode;
    private bool _isPackageOperation;
    private ComboBox? _classComboBox;
    private List<MapRecord>? _batchQueue;
    private int _batchQueueIndex;
    private BatchOperationType _batchType;
    private MapDraft? _draft;
    private Grid? _markerSurface;
    private Canvas? _markerCanvas;
    private Canvas? _markerPanelCanvas;
    private ScrollViewer? _markerHostScroller;
    private Border? _markerControlPanel;
    private Button? _markerConfirmButton;
    private string _activeFloorKey = "1f";
    private Guid? _activeAnchorId;
    private List<MapDraft>? _batchDrafts;
    private int _batchDraftIndex;
    private List<ImportFloorEntry>? _pendingImportFloors;
    private string? _selectedImportFloorKey;
    private Border? _selectedImportFloorCard;
    private readonly Dictionary<ImportFloorEntry, Border> _importFloorCards = [];
    private Grid? _importFloorCardsGrid;
    private ScrollViewer? _importFloorScrollViewer;
    private Button? _importAddFloorCard;
    private DispatcherQueueTimer? _importClickTimer;
    private DispatcherQueueTimer? _importHoldTimer;
    private DispatcherQueueTimer? _importDragFollowTimer;
    private DispatcherQueueTimer? _importDropSettleTimer;
    private ImportFloorEntry? _pendingImportClickEntry;
    private ImportFloorEntry? _draggedImportFloor;
    private Border? _draggedImportFloorCard;
    private Point _importDragStartPoint;
    private Point _importDragPointerPosition;
    private Point _importDragPointerInScrollViewer;
    private Vector3 _importDragVisualTranslation;
    private bool _isDraggingImportFloor;
    private Point? _dragStart;
    private NormalizedRectangle? _pendingMarker;
    private bool _isSelectingRecognitionRegion;
    private bool _isAnnotationPanelOpen;
    private int _selectedAnnotationColor;
    private MapAnnotationType _activeAnnotationType;
    private Point? _panelDragStart;
    private Point _panelDragOrigin;
    private Point _panelPositionRatio = new(1d, 0d);
    private double _imageAspectRatio = 16d / 9d;

    public MapListPage()
    {
        _idvmPackageService = new IdvmPackageService(_repository);
        Content = _workflowHost;
        Loaded += MapListPage_Loaded;
        Unloaded += (_, _) => DetachMarkerHostScroller();
        KeyDown += MapListPage_KeyDown;
    }

    private bool HasSelection => _selectedMapIds.Count > 0;

    private bool IsBatchOperation => _batchQueue is { Count: > 0 };

    private void ResetBatchOperation()
    {
        _batchQueue = null;
        _batchQueueIndex = 0;
        _batchType = BatchOperationType.None;
    }

    private bool TryAdvanceBatchQueue()
    {
        if (!IsBatchOperation || _batchQueue is null || _batchQueueIndex + 1 >= _batchQueue.Count)
            return false;

        _batchQueueIndex++;
        _activeFloorKey = "1f";
        _activeAnchorId = null;
        _isSelectingRecognitionRegion = false;
        _pendingMarker = null;
        _dragStart = null;
        return true;
    }

    private async Task StartBatchOperationAsync(BatchOperationType type)
    {
        if (_selectedMapIds.Count == 0)
            return;

        _batchQueue = _loadedMaps
            .Where(map => _selectedMapIds.Contains(map.Id))
            .OrderBy(map => map.SequenceNumber)
            .ToList();
        _batchQueueIndex = 0;
        _batchType = type;

        if (type == BatchOperationType.Delete)
        {
            await ExecuteBatchDeleteAsync();
            return;
        }

        var firstMap = _batchQueue[0];
        if (type == BatchOperationType.Edit)
            await EditMapAsync(firstMap);
        else
            await ImportMapAsync(firstMap);
    }

    private async Task ExecuteBatchDeleteAsync()
    {
        if (_batchQueue is null || _batchQueue.Count == 0)
            return;

        var count = _batchQueue.Count;
        var label = count == 1 ? _batchQueue[0].DisplayName : $"{count} 个地图";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除地图？",
            Content = $"将永久删除 {label} 及其图片和识别标记数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            ResetBatchOperation();
            return;
        }

        try
        {
            foreach (var map in _batchQueue)
                await _repository.DeleteAsync(map.Id);

            await MapRuntimeHost.Current.RefreshMapCacheAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("批量删除失败", exception.Message);
        }

        _selectedMapIds.Clear();
        _lastClickedMapId = null;
        ResetBatchOperation();
        await ShowListAsync();
    }

    private async Task ImportMapAsync(MapRecord map)
    {
        try
        {
            var draft = await _repository.CreateDraftAsync(map.Id);
            if (draft is null)
            {
                await ShowMessageAsync("地图不存在", "该地图已被删除，请刷新列表。");
                await ShowListAsync();
                return;
            }
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            _draft = draft;
            _draft.Recognition.EnsureStandardAnchors();
            await ShowImportAsync(_draft);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法导入地图", exception.Message);
        }
    }

    private async void MapListPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MapListPage_Loaded;
        _ = RepairMapMetadataInBackgroundAsync();
        await ShowListAsync();
    }

    private async Task RepairMapMetadataInBackgroundAsync()
    {
        try
        {
            await _repository.RepairImageMetadataAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Map metadata repair failed: {exception}");
        }
    }

    private void MapListPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.A
            && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
        {
            _selectedMapIds = GetVisibleMaps().Select(map => map.Id).ToHashSet();
            UpdateSelectedCardVisuals();
            e.Handled = true;
        }
    }

    private async Task ShowListAsync()
    {
        ResetMarkerEditorSession();
        ResetBatchOperation();
        var snapshot = await _repository.GetCatalogSnapshotAsync();
        _classes = snapshot.Classes;
        _loadedMaps = snapshot.Maps;
        _previewImages.Clear();
        if (!_classes.Any(name => string.Equals(name, _selectedClass, StringComparison.OrdinalIgnoreCase)))
            _selectedClass = _classes[0];
        ShowListFromLoadedSnapshot();
    }

    /// <summary>Renders an in-memory class filter; never performs I/O.</summary>
    private void ShowListFromLoadedSnapshot()
    {
        var maps = GetVisibleMaps();
        _selectedMapIds = _selectedMapIds
            .Where(id => maps.Any(map => map.Id == id))
            .ToHashSet();
        _lastClickedMapId = _lastClickedMapId is { } lastId && maps.Any(map => map.Id == lastId)
            ? _lastClickedMapId
            : null;
        _cardBorders.Clear();

        var root = new Grid { Margin = new Thickness(36, 31, 36, 38), MinHeight = 630 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        header.Children.Add(CreateTitle("列表"));
        header.Children.Add(CreateDescription("在此处导入地图数据，然后进行编辑"));
        root.Children.Add(header);

        var actionRow = new Grid
        {
            Margin = new Thickness(0, 24, 0, 15)
        };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24
        };
        var importButton = CreateActionButton("导入", AccentBlue);
        importButton.IsEnabled = !_isPackageOperation;
        _editButton = CreateActionButton("编辑", RecognitionRegionOrange);
        _editButton.IsEnabled = HasSelection;
        _editButton.Click += async (_, _) =>
        {
            if (HasSelection)
            {
                PlayDetailTriggerFeedback(_editButton);
                if (_selectedMapIds.Count > 1)
                    await StartBatchOperationAsync(BatchOperationType.Edit);
                else
                    await EditMapAsync(_loadedMaps.First(map => map.Id == _selectedMapIds.First()));
            }
        };
        _deleteButton = CreateActionButton("删除", DeleteRed);
        _deleteButton.IsEnabled = HasSelection;
        _deleteButton.Click += async (_, _) =>
        {
            if (HasSelection)
            {
                PlayDetailTriggerFeedback(_deleteButton);
                if (_selectedMapIds.Count > 1)
                    await StartBatchOperationAsync(BatchOperationType.Delete);
                else
                    await DeleteSelectedMapAsync(_loadedMaps.First(map => map.Id == _selectedMapIds.First()));
            }
        };
        actions.Children.Add(importButton);
        actions.Children.Add(_editButton);
        actions.Children.Add(_deleteButton);
        actions.Children.Add(CreateClassPicker());
        actionRow.Children.Add(actions);

        var exportButton = CreateActionButton("导出", AccentBlue);
        exportButton.HorizontalAlignment = HorizontalAlignment.Right;
        exportButton.IsEnabled = !_isPackageOperation && _loadedMaps.Count > 0;
        exportButton.Click += async (_, _) => await ShowExportDialogAsync(importButton, exportButton);
        Grid.SetColumn(exportButton, 2);
        actionRow.Children.Add(exportButton);

        var teachingTip = CreateImportTeachingTip(importButton, exportButton);
        importButton.Click += (_, _) =>
        {
            if (_isPackageOperation)
                return;

            PlayDetailTriggerFeedback(importButton);
            // TeachingTip can still be in its opening animation when the target
            // is clicked again. Treat the target as a toggle so the second click
            // always represents an explicit close request instead of re-opening
            // an already-open tip.
            teachingTip.IsOpen = !teachingTip.IsOpen;
        };
        Grid.SetRow(actionRow, 1);
        root.Children.Add(actionRow);
        Grid.SetRowSpan(teachingTip, 3);
        root.Children.Add(teachingTip);

        UIElement mapContent;
        if (maps.Count == 0)
        {
            var emptyState = new Grid();
            emptyState.Children.Add(new TextBlock
            {
                Text = "尚未导入地图",
                FontSize = 16,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 110, 110, 110)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
            mapContent = emptyState;
        }
        else
        {
            var cardsGrid = new Grid { Margin = new Thickness(7, 12, 7, 12) };
            for (var column = 0; column < 3; column++)
                cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < maps.Count; index++)
            {
                if (index % 3 == 0)
                    cardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var card = CreateMapCard(maps[index]);
                Grid.SetRow(card, index / 3);
                Grid.SetColumn(card, index % 3);
                cardsGrid.Children.Add(card);
            }
            mapContent = new ScrollViewer
            {
                Content = cardsGrid,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
        }

        var mapSurface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 238, 238, 238)),
            CornerRadius = new CornerRadius(14),
            MinHeight = 459,
            Child = mapContent
        };
        Grid.SetRow(mapSurface, 2);
        root.Children.Add(mapSurface);

        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        UpdateSelectedCardVisuals();
    }

    private IReadOnlyList<MapRecord> GetVisibleMaps() => _loadedMaps
        .Where(map => string.Equals(map.Class, _selectedClass, StringComparison.OrdinalIgnoreCase))
        .OrderBy(map => map.SequenceNumber)
        .ToArray();

    private FrameworkElement CreateClassPicker()
    {
        var picker = new ComboBox
        {
            Width = 205,
            MinHeight = 45,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _classComboBox = picker;
        foreach (var className in _classes)
            picker.Items.Add(CreateClassItem(className));
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, _selectedClass, StringComparison.OrdinalIgnoreCase));
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is not ComboBoxItem item || item.Tag is not string className)
                return;
            if (!string.Equals(_selectedClass, className, StringComparison.OrdinalIgnoreCase))
            {
                _selectedClass = className;
                _selectedMapIds.Clear();
                _lastClickedMapId = null;
                _isClassDeleteMode = false;
                ShowListFromLoadedSnapshot();
            }
        };

        var add = CreateClassUtilityButton(Symbol.Add, AccentBlue);
        add.Width = 48;
        add.Height = 45;
        add.Click += async (_, _) => await ShowCreateClassDialogAsync();
        var remove = CreateClassUtilityButton(Symbol.Delete, DeleteRed);
        remove.Width = 48;
        remove.Height = 45;
        remove.Click += (_, _) =>
        {
            _isClassDeleteMode = !_isClassDeleteMode;
            ShowListFromLoadedSnapshot();
        };
        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 0, 0, 0)
        };
        controls.Children.Add(picker);
        controls.Children.Add(add);
        controls.Children.Add(remove);
        return controls;
    }

    private ComboBoxItem CreateClassItem(string className)
    {
        var row = new Grid { Width = 238 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = className,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (_isClassDeleteMode)
        {
            var remove = new Button
            {
                Content = new SymbolIcon(Symbol.Delete),
                Background = new SolidColorBrush(DeleteRed),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                MinWidth = 42,
                MinHeight = 28,
                Padding = new Thickness(4),
                IsEnabled = _classes.Count > 1,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            remove.Click += async (_, _) =>
            {
                await ConfirmDeleteClassAsync(className);
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
        }
        return new ComboBoxItem { Content = row, Tag = className, MinHeight = 38 };
    }

    private static Button CreateClassUtilityButton(Symbol symbol, Color color) => new()
    {
        Content = new SymbolIcon(symbol),
        Background = new SolidColorBrush(color),
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(4)
    };

    private async Task ShowCreateClassDialogAsync()
    {
        var nameBox = new TextBox { PlaceholderText = "输入新 Class 名称", MinWidth = 220 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新建 Class",
            Content = nameBox,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(nameBox.Text);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            var created = await _repository.CreateClassAsync(nameBox.Text);
            _selectedClass = created;
            _selectedMapIds.Clear();
            _isClassDeleteMode = false;
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法创建 Class", exception.Message);
        }
    }

    private async Task ConfirmDeleteClassAsync(string className)
    {
        var confirmation = new TextBox { PlaceholderText = "输入“确认删除”" };
        var count = _loadedMaps.Count(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase));
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = $"将永久删除 Class “{className}”及其 {count} 张地图。" });
        content.Children.Add(confirmation);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除 Class",
            Content = content,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = false
        };
        confirmation.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = confirmation.Text == "确认删除";
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await _repository.DeleteClassAsync(className);
            await MapRuntimeHost.Current.RefreshMapCacheAsync();
            _isClassDeleteMode = false;
            if (string.Equals(_selectedClass, className, StringComparison.OrdinalIgnoreCase))
                _selectedClass = _classes.First(name => !string.Equals(name, className, StringComparison.OrdinalIgnoreCase));
            _selectedMapIds.Clear();
            _lastClickedMapId = null;
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("删除 Class 失败", exception.Message);
        }
    }

    private Border CreateMapCard(MapRecord map)
    {
        var card = new Border
        {
            Margin = new Thickness(11),
            Padding = new Thickness(11),
            Background = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(9)
        };
        _cardBorders[map.Id] = card;
        AttachCardInteractionFeedback(card);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previews = new Grid { Margin = new Thickness(11, 0, 11, 0), Height = 160 };
        previews.SizeChanged += (_, _) =>
        {
            if (previews.ActualWidth > 0)
                previews.Height = Math.Round(previews.ActualWidth / 1.6);
        };
        var orderedFloors = MapFloorRules.GetOrderedFloors(map);
        var firstFloorKey = orderedFloors.FirstOrDefault()?.Key ?? map.Recognition.FirstFloor.FloorKey;
        var secondFloorKey = orderedFloors.Skip(1).FirstOrDefault()?.Key ?? map.Recognition.SecondFloor.FloorKey;
        previews.Children.Add(CreatePreviewLayer(GetMapPreviewPath(map, secondFloorKey), new Thickness(16, 0, 0, 14)));
        previews.Children.Add(CreatePreviewLayer(GetMapPreviewPath(map, firstFloorKey), new Thickness(0, 14, 16, 0)));
        content.Children.Add(previews);

        var label = new TextBlock
        {
            Text = map.DisplayName,
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(label, 1);
        content.Children.Add(label);
        var readiness = new TextBlock
        {
            Text = BuildRecognitionSummary(map),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 104, 104, 104)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(readiness, 2);
        content.Children.Add(readiness);
        card.Child = content;

        card.Tapped += (_, _) => SelectMap(map);
        card.DoubleTapped += async (_, _) =>
        {
            SelectMap(map);
            await ImportMapAsync(map);
        };
        return card;
    }

    private Border CreatePreviewLayer(string path, Thickness margin)
    {
        var border = new Border
        {
            Margin = margin,
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 196, 196)),
            CornerRadius = new CornerRadius(6)
        };
        if (File.Exists(path))
        {
            var image = new Image
            {
                Stretch = Stretch.UniformToFill
            };
            image.Loaded += (_, _) => image.Source ??= GetPreviewBitmap(path);
            border.Child = image;
        }
        return border;
    }

    private string GetMapPreviewPath(MapRecord map, string floorKey)
    {
        if (MapFloorRules.GetOrderedFloors(map).All(floor => !string.Equals(floor.Key, floorKey, StringComparison.Ordinal)))
            return string.Empty;
        var thumbnailPath = _repository.GetFloorThumbnailPath(map, floorKey);
        return File.Exists(thumbnailPath)
            ? thumbnailPath
            : _repository.GetFloorRecognitionPath(map, floorKey);
    }

    private BitmapImage GetPreviewBitmap(string path)
    {
        if (_previewImages.TryGetValue(path, out var bitmap))
            return bitmap;
        bitmap = CreateBitmap(path, decodePixelWidth: 400);
        _previewImages[path] = bitmap;
        return bitmap;
    }

    private void SelectMap(MapRecord map)
    {
        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (isCtrl)
        {
            // Ctrl+Click: toggle selection
            if (_selectedMapIds.Contains(map.Id))
                _selectedMapIds.Remove(map.Id);
            else
                _selectedMapIds.Add(map.Id);
            _lastClickedMapId = map.Id;
        }
        else if (isShift && _lastClickedMapId is { } lastId)
        {
            // Shift+Click: range select from last clicked to this item
            var orderedIds = _loadedMaps.Select(m => m.Id).ToList();
            var lastIndex = orderedIds.IndexOf(lastId);
            var currentIndex = orderedIds.IndexOf(map.Id);
            if (lastIndex >= 0 && currentIndex >= 0)
            {
                var start = Math.Min(lastIndex, currentIndex);
                var end = Math.Max(lastIndex, currentIndex);
                for (var i = start; i <= end; i++)
                    _selectedMapIds.Add(orderedIds[i]);
            }
            // Don't update _lastClickedMapId on shift-click to allow extending range
        }
        else
        {
            // Plain click: single select
            _selectedMapIds = [map.Id];
            _lastClickedMapId = map.Id;
        }

        UpdateSelectedCardVisuals();
    }

    private void UpdateSelectedCardVisuals()
    {
        foreach (var (id, card) in _cardBorders)
        {
            var selected = _selectedMapIds.Contains(id);
            card.Background = new SolidColorBrush(selected
                ? Color.FromArgb(255, 232, 242, 255)
                : Color.FromArgb(255, 245, 245, 245));
            card.BorderBrush = new SolidColorBrush(selected ? AccentBlue : Color.FromArgb(0, 0, 0, 0));
        }

        if (_editButton is not null)
            _editButton.IsEnabled = HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = HasSelection;
    }

    private async Task EditMapAsync(MapRecord map)
    {
        try
        {
            var draft = await _repository.CreateDraftAsync(map.Id);
            if (draft is null)
            {
                await ShowMessageAsync("地图不存在", "该地图已被删除，请刷新列表。");
                await ShowListAsync();
                return;
            }
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            if (!IsBatchOperation)
                ResetBatchImport();
            _draft = draft;
            _draft.Recognition.EnsureStandardAnchors();
            ShowMarkerEditor();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法编辑地图", exception.Message);
        }
    }

    private async Task DeleteSelectedMapAsync(MapRecord map)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除地图？",
            Content = $"将永久删除 {map.DisplayName} 及其两张图片和识别标记数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await _repository.DeleteAsync(map.Id);
            await MapRuntimeHost.Current.RefreshMapCacheAsync(map.Id);
            _selectedMapIds.Remove(map.Id);
            if (_lastClickedMapId == map.Id)
                _lastClickedMapId = null;
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("删除失败", exception.Message);
        }
    }

    private async Task<FloorIdentity?> ShowFloorIdentityDialogAsync(ImportFloorEntry? existing = null)
    {
        var idBox = new TextBox
        {
            Text = existing?.FloorKey ?? string.Empty,
            PlaceholderText = "例如：1f、b1、roof",
            Height = 36
        };
        var nameBox = new TextBox
        {
            Text = existing?.DisplayName ?? string.Empty,
            PlaceholderText = "例如：一楼、地下室",
            Height = 36
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "ID（只能包含英文字母和数字）",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
        });
        panel.Children.Add(idBox);
        panel.Children.Add(new TextBlock
        {
            Text = "名称",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
        });
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设定楼层 ID 与名称",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        void UpdateValidation()
        {
            var key = idBox.Text.Trim();
            var isValid = key.Length > 0 && key.All(char.IsAsciiLetterOrDigit);
            var isDuplicate = _pendingImportFloors?.Any(entry =>
                !ReferenceEquals(entry, existing)
                && string.Equals(entry.FloorKey, key, StringComparison.OrdinalIgnoreCase)) is true;
            dialog.IsPrimaryButtonEnabled = isValid && !isDuplicate;
        }

        idBox.TextChanged += (_, _) =>
        {
            var filtered = new string(idBox.Text.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (filtered != idBox.Text)
            {
                var cursor = idBox.SelectionStart;
                idBox.Text = filtered;
                idBox.SelectionStart = Math.Min(cursor, filtered.Length);
            }
            UpdateValidation();
        };
        UpdateValidation();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var key = new string(idBox.Text.Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
        var displayName = nameBox.Text.Trim();

        if (key.Length == 0)
            return null;

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = key;

        return new FloorIdentity(key, displayName);
    }

    private async Task ShowImportAsync(MapDraft draft)
    {
        CancelPendingImportClick();
        ResetImportFloorDragSession(animateReturn: false);
        _draft = draft;
        _selectedImportFloorKey = null;
        _selectedImportFloorCard = null;
        _pendingImportFloors = draft.FloorPaths.Count > 0
            ? (draft.Floors.Count > 0
                ? draft.Floors.OrderBy(floor => floor.SortOrder)
                    .Where(floor => draft.FloorPaths.ContainsKey(floor.Key))
                    .Select(floor => new { floor.Key, floor.DisplayName })
                : draft.FloorPaths.Select(kvp => new { Key = kvp.Key, DisplayName = kvp.Key }))
                .Select(floor => new ImportFloorEntry
            {
                OriginalFloorKey = floor.Key,
                FloorKey = floor.Key,
                DisplayName = floor.DisplayName,
                ImagePath = draft.FloorPaths[floor.Key],
                PreviewImagePath = draft.FloorPreviewPaths.TryGetValue(floor.Key, out var previewPath)
                    ? previewPath
                    : draft.FloorPaths[floor.Key]
            }).ToList()
            : [];
        draft.Recognition.EnsureStandardAnchors();

        var root = new Grid
        {
            Margin = new Thickness(36, 31, 36, 38),
            MinHeight = 630,
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255))
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Header ──
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        titleBlock.Children.Add(CreateTitle("导入地图"));
        titleBlock.Children.Add(CreateDescription("为地图添加楼层图片。点击下方占位符开始，每层可设定自定义 ID 与名称。"));
        if (IsBatchImport)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量导入：第 {_batchDraftIndex + 1} / {_batchDrafts!.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        else if (IsBatchOperation && _batchQueue is not null)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量{(_batchType == BatchOperationType.Import ? "导入" : "编辑")}：第 {_batchQueueIndex + 1} / {_batchQueue.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        header.Children.Add(titleBlock);
        var backButton = CreateSecondaryButton("返回列表");
        backButton.Click += async (_, _) =>
        {
            CancelPendingImportClick();
            ResetImportFloorDragSession(animateReturn: false);
            ResetBatchImport();
            ResetBatchOperation();
            _pendingImportFloors = null;
            await ShowListAsync();
        };
        Grid.SetColumn(backButton, 1);
        header.Children.Add(backButton);
        root.Children.Add(header);

        // ── Floor area ──
        var floorAreaContainer = new Grid
        {
            Margin = new Thickness(0, 34, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var floorScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _importFloorScrollViewer = floorScrollViewer;
        floorAreaContainer.Children.Add(floorScrollViewer);
        Grid.SetRow(floorAreaContainer, 1);
        root.Children.Add(floorAreaContainer);

        // ── Confirm button ──
        var continueButton = CreateActionButton("确认", AccentBlue);
        continueButton.HorizontalAlignment = HorizontalAlignment.Center;
        continueButton.Width = 284;
        continueButton.IsEnabled = _pendingImportFloors.Count > 0;
        continueButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(continueButton);
            CancelPendingImportClick();
            ResetImportFloorDragSession(animateReturn: false);
            CommitPendingFloorsToDraft();
            ShowMarkerEditor();
        };
        Grid.SetRow(continueButton, 2);
        root.Children.Add(continueButton);

        // ── Local: rebuild floor list UI ──
        void RenderFloorArea()
        {
            const int cardsPerRow = 4;
            var entries = _pendingImportFloors ?? [];
            var totalCards = entries.Count + 1; // existing floors plus the add tile
            var cardsGrid = new Grid
            {
                ColumnSpacing = 18,
                RowSpacing = 18,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _importFloorCardsGrid = cardsGrid;
            _importFloorCards.Clear();
            _importAddFloorCard = null;
            for (var column = 0; column < cardsPerRow; column++)
                cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < (totalCards + cardsPerRow - 1) / cardsPerRow; row++)
                cardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var index = 0; index < entries.Count; index++)
            {
                var card = CreateImportFloorCard(entries[index], RenderFloorArea, continueButton);
                Grid.SetRow(card, index / cardsPerRow);
                Grid.SetColumn(card, index % cardsPerRow);
                cardsGrid.Children.Add(card);
            }

            var addCard = CreateAddFloorButton(RenderFloorArea, continueButton);
            Grid.SetRow(addCard, entries.Count / cardsPerRow);
            Grid.SetColumn(addCard, entries.Count % cardsPerRow);
            cardsGrid.Children.Add(addCard);
            _importAddFloorCard = addCard;

            floorScrollViewer.Content = cardsGrid;
            UpdateImportFloorGridLayout();
        }

        RenderFloorArea();
        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        await Task.CompletedTask;
    }

    /// <summary>Transfers <see cref="_pendingImportFloors"/> into <see cref="_draft"/>.</summary>
    private void CommitPendingFloorsToDraft()
    {
        if (_draft is null || _pendingImportFloors is null)
            return;

        _draft.Recognition.EnsureStandardAnchors();
        var profilesByKey = new Dictionary<string, FloorRecognitionProfile>(
            _draft.Recognition.Floors,
            StringComparer.OrdinalIgnoreCase);
        var legacyFirstProfile = _draft.Recognition.FirstFloor;
        var legacySecondProfile = _draft.Recognition.SecondFloor;

        _draft.FloorPaths.Clear();
        _draft.Floors.Clear();
        var profilesByNewKey = new Dictionary<string, FloorRecognitionProfile>(
            StringComparer.OrdinalIgnoreCase);
        FloorRecognitionProfile? firstProfile = null;
        FloorRecognitionProfile? secondProfile = null;

        for (var i = 0; i < _pendingImportFloors.Count; i++)
        {
            var entry = _pendingImportFloors[i];
            var profile = profilesByKey.GetValueOrDefault(entry.OriginalFloorKey)
                ?? (entry.OriginalFloorKey.Equals("1f", StringComparison.OrdinalIgnoreCase)
                    ? legacyFirstProfile
                    : entry.OriginalFloorKey.Equals("2f", StringComparison.OrdinalIgnoreCase)
                        ? legacySecondProfile
                        : null)
                ?? new FloorRecognitionProfile();
            profile.FloorKey = entry.FloorKey;
            profile.Floor = i == 0 ? MapFloor.First : MapFloor.Second;

            _draft.FloorPaths[entry.FloorKey] = entry.ImagePath;
            _draft.Floors.Add(new FloorDefinition
            {
                Key = entry.FloorKey,
                DisplayName = entry.DisplayName,
                SortOrder = i + 1
            });
            profilesByNewKey[entry.FloorKey] = profile;

            // 向后兼容：填充 FloorOnePath / FloorTwoPath
            if (i == 0)
            {
                _draft.FloorOnePath = entry.ImagePath;
                firstProfile = profile;
            }
            else if (i == 1)
            {
                _draft.FloorTwoPath = entry.ImagePath;
                secondProfile = profile;
            }
        }

        // 如果只有一个楼层，第二个楼层回退到第一个
        if (_pendingImportFloors.Count == 1)
        {
            _draft.FloorTwoPath = _draft.FloorOnePath;
            secondProfile = firstProfile;
        }

        _draft.Recognition.FirstFloor = firstProfile ?? legacyFirstProfile;
        _draft.Recognition.SecondFloor = secondProfile ?? firstProfile ?? legacySecondProfile;
        _draft.Recognition.Floors = profilesByNewKey;
        _draft.Recognition.EnsureStandardAnchors();
    }

    private Button CreateAddFloorButton(Action onChanged, Button confirmButton)
    {
        var placeholderIcon = new SymbolIcon
        {
            Symbol = Symbol.Add,
            Width = 56,
            Height = 56,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var placeholderLabel = new TextBlock
        {
            Text = "添加楼层图片",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var iconSurface = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 0
        };
        iconSurface.Children.Add(placeholderIcon);
        iconSurface.Children.Add(placeholderLabel);

        // 图片占位区域 — 匹配 CreateImagePicker 的结构
        var imagePlaceholder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)),
            CornerRadius = new CornerRadius(7),
            Child = iconSurface,
            Height = 205,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 名称占位区域（与楼层卡片的名称对齐）
        var namePlaceholder = new TextBlock
        {
            Text = " ",
            FontSize = 14,
            Height = 24,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var content = new Grid { RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(imagePlaceholder);
        Grid.SetRow(namePlaceholder, 1);
        content.Children.Add(namePlaceholder);

        // 外层卡片 — 背景和圆角在 Border 上，不在 Button 上
        var cardSurface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };

        var card = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = cardSurface,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AttachCardInteractionFeedback(card);

        card.Click += async (_, _) =>
        {
            card.IsEnabled = false;
            try
            {
                var selectedPath = await PickImageAsync("选择楼层地图");
                if (selectedPath is null)
                    return;

                var identity = await ShowFloorIdentityDialogAsync();
                if (identity is null)
                    return;

                var entry = new ImportFloorEntry
                {
                    OriginalFloorKey = identity.FloorKey,
                    FloorKey = identity.FloorKey,
                    DisplayName = identity.DisplayName,
                    ImagePath = selectedPath,
                    PreviewImagePath = selectedPath
                };
                _pendingImportFloors ??= [];
                _pendingImportFloors.Add(entry);
                _selectedImportFloorKey = entry.FloorKey;
                confirmButton.IsEnabled = _pendingImportFloors.Count > 0;
                onChanged();
            }
            finally
            {
                card.IsEnabled = true;
            }
        };

        return card;
    }

    private Border CreateImportFloorCard(ImportFloorEntry entry, Action onChanged, Button confirmButton)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var previewPath = MapRepository.IsSupportedImage(entry.PreviewImagePath)
            && File.Exists(entry.PreviewImagePath)
            ? entry.PreviewImagePath
            : entry.ImagePath;
        if (MapRepository.IsSupportedImage(previewPath) && File.Exists(previewPath))
            image.Source = CreateBitmap(previewPath);

        var deleteButton = new Button
        {
            Content = "✕",
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontSize = 12,
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0)
        };
        deleteButton.Click += (_, _) =>
        {
            _pendingImportFloors?.Remove(entry);
            if (_selectedImportFloorKey == entry.FloorKey)
            {
                _selectedImportFloorKey = null;
                _selectedImportFloorCard = null;
            }
            confirmButton.IsEnabled = _pendingImportFloors?.Count > 0;
            onChanged();
        };
        deleteButton.PointerPressed += (_, e) => e.Handled = true;
        deleteButton.PointerMoved += (_, e) => e.Handled = true;
        deleteButton.PointerReleased += (_, e) => e.Handled = true;

        var imageSurface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = image
        };

        var overlay = new Grid();
        overlay.Children.Add(imageSurface);
        overlay.Children.Add(deleteButton);

        var imageFrame = new Border
        {
            CornerRadius = new CornerRadius(7),
            Child = overlay,
            MinHeight = 175,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        imageFrame.SizeChanged += (_, _) =>
        {
            if (imageFrame.ActualWidth <= 0)
                return;
            imageFrame.Height = Math.Max(175, Math.Round(imageFrame.ActualWidth / 1.6));
            imageFrame.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Rect(0, 0, imageFrame.ActualWidth, imageFrame.ActualHeight)
            };
        };

        var nameLabel = new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var content = new Grid { RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(imageFrame);
        Grid.SetRow(nameLabel, 1);
        content.Children.Add(nameLabel);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
            BorderBrush = new SolidColorBrush(
                _selectedImportFloorKey == entry.FloorKey ? AccentBlue : Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
        card.PointerPressed += (_, _) => SelectImportFloorCard(entry.FloorKey, card);
        AttachImportFloorCardInteraction(card, entry, onChanged, confirmButton);
        _importFloorCards[entry] = card;
        return card;
    }

    private void SelectImportFloorCard(string floorKey, Border card)
    {
        _selectedImportFloorKey = floorKey;
        if (_selectedImportFloorCard is not null && _selectedImportFloorCard != card)
            _selectedImportFloorCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        _selectedImportFloorCard = card;
        card.BorderBrush = new SolidColorBrush(AccentBlue);
    }

    private void AttachImportFloorCardInteraction(
        Border card,
        ImportFloorEntry entry,
        Action onChanged,
        Button confirmButton)
    {
        var isPressed = false;
        var pressCanceled = false;
        var isReleasingCapture = false;
        var isSecondTap = false;

        card.PointerEntered += (_, _) =>
        {
            if (!isPressed)
                PlayHoverFeedback(card, 1.01f, TimeSpan.FromMilliseconds(150));
        };
        card.PointerExited += (_, _) =>
        {
            if (isPressed)
                pressCanceled = true;
            if (!isPressed || !_isDraggingImportFloor)
                PlayHoverFeedback(card, 1f, TimeSpan.FromMilliseconds(100));
        };
        card.PointerPressed += (_, e) =>
        {
            isPressed = true;
            pressCanceled = false;
            isSecondTap = BeginImportFloorPointerPress(entry, onChanged);
            _draggedImportFloor = entry;
            _draggedImportFloorCard = card;
            _importDragStartPoint = e.GetCurrentPoint(card).Position;
            _isDraggingImportFloor = false;
            SelectImportFloorCard(entry.FloorKey, card);
            card.CapturePointer(e.Pointer);
            UpdateImportDragPointer(e, card);
            PlayHoverFeedback(card, 0.975f, TimeSpan.FromMilliseconds(80));
            StartImportFloorHoldTimer(() =>
            {
                if (!isPressed || !ReferenceEquals(_draggedImportFloor, entry))
                    return;
                pressCanceled = true;
                BeginImportFloorDrag(entry, card);
            });
        };
        card.PointerMoved += (_, e) =>
        {
            if (!isPressed || _draggedImportFloor != entry)
                return;

            UpdateImportDragPointer(e, card);
            if (_isDraggingImportFloor)
                UpdateImportDragFrame();
        };
        card.PointerReleased += (_, e) =>
        {
            if (!isPressed)
                return;

            isPressed = false;
            StopImportFloorHoldTimer();
            isReleasingCapture = true;
            card.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;

            var wasDragging = _isDraggingImportFloor;
            if (wasDragging)
                ResetImportFloorDragSession(animateReturn: true);
            else
            {
                ClearImportFloorDragCandidate();
                PlayHoverFeedback(card, 1f, TimeSpan.FromMilliseconds(110));
            }

            if (!wasDragging && !pressCanceled)
            {
                if (isSecondTap)
                    _ = ReplaceImportFloorImageAsync(entry, onChanged, confirmButton);
                else
                    QueueImportFloorClick(entry, onChanged, confirmButton);
            }
        };
        card.PointerCanceled += (_, e) =>
        {
            isPressed = false;
            pressCanceled = true;
            StopImportFloorHoldTimer();
            isReleasingCapture = true;
            card.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;
            ResetImportFloorDragSession(animateReturn: _isDraggingImportFloor);
        };
        card.PointerCaptureLost += (_, _) =>
        {
            if (isReleasingCapture)
                return;
            isPressed = false;
            pressCanceled = true;
            StopImportFloorHoldTimer();
            ResetImportFloorDragSession(animateReturn: _isDraggingImportFloor);
        };
    }

    private bool BeginImportFloorPointerPress(ImportFloorEntry entry, Action onChanged)
    {
        if (_pendingImportClickEntry is not null
            && ReferenceEquals(_pendingImportClickEntry, entry)
            && _importClickTimer is not null)
        {
            CancelPendingImportClick();
            return true;
        }

        if (_pendingImportClickEntry is { } previous)
        {
            CancelPendingImportClick();
            _ = EditImportFloorAsync(previous, onChanged);
        }

        return false;
    }

    private void StartImportFloorHoldTimer(Action onActivated)
    {
        StopImportFloorHoldTimer();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_importHoldTimer, timer))
                return;
            _importHoldTimer = null;
            onActivated();
        };
        _importHoldTimer = timer;
        timer.Start();
    }

    private void StopImportFloorHoldTimer()
    {
        _importHoldTimer?.Stop();
        _importHoldTimer = null;
    }

    private void BeginImportFloorDrag(ImportFloorEntry entry, Border card)
    {
        if (!ReferenceEquals(_draggedImportFloor, entry)
            || !ReferenceEquals(_draggedImportFloorCard, card))
            return;

        StopImportFloorHoldTimer();
        StopImportFloorDropSettleTimer();
        CancelPendingImportClick();
        _isDraggingImportFloor = true;
        Canvas.SetZIndex(card, 100);
        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.StopAnimation("Translation");
        _importDragVisualTranslation = Vector3.Zero;
        StartVisualTranslation(
            visual,
            Vector3.Zero,
            Vector3.Zero,
            TimeSpan.FromMilliseconds(1));
        PlayHoverFeedback(card, 1.04f, TimeSpan.FromMilliseconds(130));
        StartImportFloorFollowTimer();
        UpdateImportDragFrame();
    }

    private void StartImportFloorFollowTimer()
    {
        StopImportFloorFollowTimer();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => UpdateImportDragFrame();
        _importDragFollowTimer = timer;
        timer.Start();
    }

    private void StopImportFloorFollowTimer()
    {
        _importDragFollowTimer?.Stop();
        _importDragFollowTimer = null;
    }

    private void UpdateImportDragPointer(PointerRoutedEventArgs e, UIElement fallbackTarget)
    {
        _importDragPointerPosition = _importFloorCardsGrid is { } grid
            ? e.GetCurrentPoint(grid).Position
            : e.GetCurrentPoint(fallbackTarget).Position;
        if (_importFloorScrollViewer is { } scrollViewer)
            _importDragPointerInScrollViewer = e.GetCurrentPoint(scrollViewer).Position;
    }

    private Point GetImportDragPointerPosition()
    {
        if (_importFloorScrollViewer is { } scrollViewer
            && _importFloorCardsGrid is { } grid)
        {
            return scrollViewer.TransformToVisual(grid)
                .TransformPoint(_importDragPointerInScrollViewer);
        }
        return _importDragPointerPosition;
    }

    private void UpdateImportDragFrame()
    {
        if (!_isDraggingImportFloor
            || _draggedImportFloor is not { } entry
            || _draggedImportFloorCard is not { } card
            || _importFloorCardsGrid is not { } grid)
            return;

        UpdateImportFloorAutoScroll();
        var pointerPosition = GetImportDragPointerPosition();
        ReorderImportFloorCard(entry, pointerPosition);

        var layoutOrigin = card.TransformToVisual(grid).TransformPoint(new Point(0, 0));
        var targetTranslation = new Vector3(
            (float)(pointerPosition.X - layoutOrigin.X - _importDragStartPoint.X),
            (float)(pointerPosition.Y - layoutOrigin.Y - _importDragStartPoint.Y),
            0);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        var nextTranslation = Vector3.Lerp(
            _importDragVisualTranslation,
            targetTranslation,
            0.36f);
        StartVisualTranslation(
            visual,
            _importDragVisualTranslation,
            nextTranslation,
            TimeSpan.FromMilliseconds(16));
        _importDragVisualTranslation = nextTranslation;
    }

    private void UpdateImportFloorAutoScroll()
    {
        if (_importFloorScrollViewer is not { } scrollViewer || scrollViewer.ActualHeight <= 0)
            return;

        const double edgeThreshold = 48d;
        const double pixelsPerTick = 18d;
        var pointerY = _importDragPointerInScrollViewer.Y;
        var delta = pointerY < edgeThreshold
            ? -pixelsPerTick * (1d - (pointerY / edgeThreshold))
            : pointerY > scrollViewer.ActualHeight - edgeThreshold
                ? pixelsPerTick * (1d - ((scrollViewer.ActualHeight - pointerY) / edgeThreshold))
                : 0d;
        if (Math.Abs(delta) < 0.01d)
            return;

        var nextOffset = Math.Clamp(
            scrollViewer.VerticalOffset + delta,
            0d,
            scrollViewer.ScrollableHeight);
        scrollViewer.ChangeView(null, nextOffset, null, disableAnimation: true);
    }

    private void ClearImportFloorDragCandidate()
    {
        _isDraggingImportFloor = false;
        _draggedImportFloor = null;
        _draggedImportFloorCard = null;
    }

    private void ResetImportFloorDragSession(bool animateReturn)
    {
        StopImportFloorHoldTimer();
        StopImportFloorFollowTimer();
        StopImportFloorDropSettleTimer();
        var card = _draggedImportFloorCard;
        ClearImportFloorDragCandidate();
        if (card is null)
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.StopAnimation("Translation");
        if (animateReturn)
        {
            StartVisualTranslation(
                visual,
                _importDragVisualTranslation,
                Vector3.Zero,
                TimeSpan.FromMilliseconds(180));
            ScheduleImportFloorDropSettle(card);
        }
        else
        {
            StartVisualTranslation(
                visual,
                Vector3.Zero,
                Vector3.Zero,
                TimeSpan.FromMilliseconds(1));
            Canvas.SetZIndex(card, 0);
        }
        _importDragVisualTranslation = Vector3.Zero;
        PlayHoverFeedback(card, 1f, TimeSpan.FromMilliseconds(160));
    }

    private void ScheduleImportFloorDropSettle(Border card)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(190);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_importDropSettleTimer, timer))
                return;
            _importDropSettleTimer = null;
            Canvas.SetZIndex(card, 0);
        };
        _importDropSettleTimer = timer;
        timer.Start();
    }

    private void StopImportFloorDropSettleTimer()
    {
        _importDropSettleTimer?.Stop();
        _importDropSettleTimer = null;
    }

    private void QueueImportFloorClick(
        ImportFloorEntry entry,
        Action onChanged,
        Button confirmButton)
    {
        if (_pendingImportClickEntry is not null
            && ReferenceEquals(_pendingImportClickEntry, entry)
            && _importClickTimer is not null)
        {
            CancelPendingImportClick();
            _ = ReplaceImportFloorImageAsync(entry, onChanged, confirmButton);
            return;
        }

        if (_pendingImportClickEntry is { } previous)
        {
            CancelPendingImportClick();
            _ = EditImportFloorAsync(previous, onChanged);
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(280);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_importClickTimer, timer))
                return;
            _importClickTimer = null;
            _pendingImportClickEntry = null;
            _ = EditImportFloorAsync(entry, onChanged);
        };
        _pendingImportClickEntry = entry;
        _importClickTimer = timer;
        timer.Start();
    }

    private void CancelPendingImportClick()
    {
        _importClickTimer?.Stop();
        _importClickTimer = null;
        _pendingImportClickEntry = null;
    }

    private async Task EditImportFloorAsync(ImportFloorEntry entry, Action onChanged)
    {
        if (_pendingImportFloors?.Contains(entry) is not true)
            return;

        var identity = await ShowFloorIdentityDialogAsync(entry);
        if (identity is null)
            return;

        var oldKey = entry.FloorKey;
        entry.FloorKey = identity.FloorKey;
        entry.DisplayName = identity.DisplayName;
        if (string.Equals(_selectedImportFloorKey, oldKey, StringComparison.OrdinalIgnoreCase))
            _selectedImportFloorKey = entry.FloorKey;
        onChanged();
    }

    private async Task ReplaceImportFloorImageAsync(
        ImportFloorEntry entry,
        Action onChanged,
        Button confirmButton)
    {
        if (_pendingImportFloors?.Contains(entry) is not true)
            return;

        var selectedPath = await PickImageAsync("替换楼层图片");
        if (selectedPath is null)
            return;

        entry.ImagePath = selectedPath;
        entry.PreviewImagePath = selectedPath;
        confirmButton.IsEnabled = _pendingImportFloors.Count > 0;
        onChanged();
    }

    private void ReorderImportFloorCard(ImportFloorEntry entry, Point pointerPosition)
    {
        if (_pendingImportFloors is not { Count: > 1 } entries
            || _importFloorCardsGrid is not { } grid)
            return;

        var currentIndex = entries.IndexOf(entry);
        if (currentIndex < 0)
            return;

        var remaining = entries.Where(candidate => !ReferenceEquals(candidate, entry)).ToList();
        var insertIndex = Math.Clamp(currentIndex, 0, remaining.Count);
        if (_importAddFloorCard is { } addCard
            && TryGetImportFloorCardRect(addCard, grid, out var addCardRect)
            && addCardRect.Contains(pointerPosition))
        {
            insertIndex = remaining.Count;
        }
        for (var index = 0; index < remaining.Count; index++)
        {
            if (!_importFloorCards.TryGetValue(remaining[index], out var targetCard))
                continue;

            if (!TryGetImportFloorCardRect(targetCard, grid, out var targetRect))
                continue;
            var midpointY = targetRect.Top + (targetRect.Height / 2d);
            if (pointerPosition.X >= targetRect.Left
                && pointerPosition.X <= targetRect.Right
                && pointerPosition.Y >= targetRect.Top
                && pointerPosition.Y <= targetRect.Bottom)
            {
                insertIndex = pointerPosition.Y < midpointY ? index : index + 1;
                break;
            }
        }

        if (pointerPosition.Y > grid.ActualHeight)
            insertIndex = remaining.Count;
        else if (pointerPosition.Y < 0d)
            insertIndex = 0;

        var projected = FloorOrderProjection.MoveToInsertion(entries, entry, insertIndex);
        if (entries.SequenceEqual(projected))
            return;

        entries.Clear();
        entries.AddRange(projected);
        UpdateImportFloorGridLayout(animateReflow: true);
    }

    private static bool TryGetImportFloorCardRect(UIElement element, UIElement relativeTo, out Rect rect)
    {
        if (element is not FrameworkElement frameworkElement
            || frameworkElement.ActualWidth <= 0d
            || frameworkElement.ActualHeight <= 0d)
        {
            rect = default;
            return false;
        }

        var origin = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        rect = new Rect(origin.X, origin.Y, frameworkElement.ActualWidth, frameworkElement.ActualHeight);
        return true;
    }

    private void UpdateImportFloorGridLayout(bool animateReflow = false)
    {
        if (_importFloorCardsGrid is not { } grid || _pendingImportFloors is not { } entries)
            return;

        var previousPositions = animateReflow
            ? CaptureImportFloorLayoutPositions(grid)
            : null;
        const int cardsPerRow = 4;
        var totalCards = entries.Count + 1;
        grid.RowDefinitions.Clear();
        for (var row = 0; row < (totalCards + cardsPerRow - 1) / cardsPerRow; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < entries.Count; index++)
        {
            if (_importFloorCards.TryGetValue(entries[index], out var card))
            {
                Grid.SetRow(card, index / cardsPerRow);
                Grid.SetColumn(card, index % cardsPerRow);
            }
        }

        if (_importAddFloorCard is not null)
        {
            Grid.SetRow(_importAddFloorCard, entries.Count / cardsPerRow);
            Grid.SetColumn(_importAddFloorCard, entries.Count % cardsPerRow);
        }

        if (previousPositions is { Count: > 0 })
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_importFloorCardsGrid, grid))
                    return;
                AnimateImportFloorReflow(grid, previousPositions);
            });
        }
    }

    private Dictionary<UIElement, Point> CaptureImportFloorLayoutPositions(Grid grid)
    {
        var positions = new Dictionary<UIElement, Point>();
        foreach (var card in _importFloorCards.Values)
        {
            if (TryGetImportFloorCardRect(card, grid, out var rect))
                positions[card] = new Point(rect.X, rect.Y);
        }
        if (_importAddFloorCard is { } addCard
            && TryGetImportFloorCardRect(addCard, grid, out var addRect))
        {
            positions[addCard] = new Point(addRect.X, addRect.Y);
        }
        return positions;
    }

    private void AnimateImportFloorReflow(Grid grid, IReadOnlyDictionary<UIElement, Point> previousPositions)
    {
        foreach (var (element, previousPosition) in previousPositions)
        {
            if (ReferenceEquals(element, _draggedImportFloorCard)
                || !TryGetImportFloorCardRect(element, grid, out var currentRect))
                continue;

            var delta = new Vector3(
                (float)(previousPosition.X - currentRect.X),
                (float)(previousPosition.Y - currentRect.Y),
                0);
            if (delta.LengthSquared() < 0.01f)
                continue;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            StartVisualTranslation(
                visual,
                delta,
                Vector3.Zero,
                TimeSpan.FromMilliseconds(170));
        }
    }

    private static void StartVisualTranslation(
        Microsoft.UI.Composition.Visual visual,
        Vector3 from,
        Vector3 to,
        TimeSpan duration)
    {
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, CreateMainEase(visual));
        animation.Duration = duration;
        visual.StartAnimation("Translation", animation);
    }

    private static SymbolIcon CreatePickerPlaceholder() => new()
    {
        Symbol = Symbol.Add,
        Width = 56,
        Height = 56,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button CreateImagePicker(Image preview, UIElement placeholder)
    {
        var content = new Grid();
        content.Children.Add(preview);
        content.Children.Add(placeholder);
        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)),
            CornerRadius = new CornerRadius(7),
            Child = content
        };
        var button = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = surface
        };
        button.SizeChanged += (_, _) =>
        {
            if (button.ActualWidth > 0)
                button.Height = Math.Round(button.ActualWidth / 1.6);
        };
        return button;
    }

    private static void SetPickerPreview(Image image, UIElement placeholder, string? path)
    {
        var hasImage = MapRepository.IsSupportedImage(path) && File.Exists(path);
        image.Source = hasImage ? CreateBitmap(path!) : null;
        image.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AttachImageDropTarget(UIElement target, Func<IReadOnlyList<string>, Task> onImagesDropped)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "释放以导入图片";
            e.Handled = true;
        };
        target.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
            try
            {
                var paths = await GetDroppedImagePathsAsync(e);
                if (paths.Count == 0)
                {
                    await ShowMessageAsync("没有可导入的图片", "请拖入有效的 PNG、JPG 或 JPEG 图片。");
                    return;
                }
                await onImagesDropped(paths);
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("导入失败", exception.Message);
            }
        };
    }

    private static async Task<IReadOnlyList<string>> GetDroppedImagePathsAsync(DragEventArgs e)
    {
        var items = await e.DataView.GetStorageItemsAsync();
        var imageFiles = new List<StorageFile>();
        foreach (var item in items)
        {
            if (item is StorageFile file
                && MapRepository.IsSupportedImage(file.Path)
                && await IsReadableImageAsync(file))
            {
                imageFiles.Add(file);
            }
        }

        return imageFiles
            .OrderBy(file => file.Name, NaturalFileNameComparer.Instance)
            .Select(file => file.Path)
            .ToArray();
    }

    private static async Task<bool> IsReadableImageAsync(StorageFile file)
    {
        try
        {
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            _ = await BitmapDecoder.CreateAsync(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ImportFloorEntry
    {
        public string OriginalFloorKey { get; set; } = string.Empty;
        public string FloorKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public string PreviewImagePath { get; set; } = string.Empty;
    }

    private sealed record FloorIdentity(string FloorKey, string DisplayName);

    private sealed class NaturalFileNameComparer : IComparer<string>
    {
        public static NaturalFileNameComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            var leftName = System.IO.Path.GetFileNameWithoutExtension(left ?? string.Empty);
            var rightName = System.IO.Path.GetFileNameWithoutExtension(right ?? string.Empty);
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < leftName.Length && rightIndex < rightName.Length)
            {
                if (char.IsDigit(leftName[leftIndex]) && char.IsDigit(rightName[rightIndex]))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < leftName.Length && char.IsDigit(leftName[leftIndex]))
                        leftIndex++;
                    while (rightIndex < rightName.Length && char.IsDigit(rightName[rightIndex]))
                        rightIndex++;

                    var leftDigits = leftName[leftStart..leftIndex].TrimStart('0');
                    var rightDigits = rightName[rightStart..rightIndex].TrimStart('0');
                    leftDigits = leftDigits.Length == 0 ? "0" : leftDigits;
                    rightDigits = rightDigits.Length == 0 ? "0" : rightDigits;
                    var numberComparison = leftDigits.Length.CompareTo(rightDigits.Length);
                    if (numberComparison != 0)
                        return numberComparison;
                    numberComparison = string.Compare(leftDigits, rightDigits, StringComparison.Ordinal);
                    if (numberComparison != 0)
                        return numberComparison;
                    continue;
                }

                var characterComparison = char.ToUpperInvariant(leftName[leftIndex])
                    .CompareTo(char.ToUpperInvariant(rightName[rightIndex]));
                if (characterComparison != 0)
                    return characterComparison;
                leftIndex++;
                rightIndex++;
            }

            var lengthComparison = leftName.Length.CompareTo(rightName.Length);
            return lengthComparison != 0
                ? lengthComparison
                : StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        }
    }

    private async Task ImportDroppedImagesAsync(
        IReadOnlyList<string> paths,
        MapDraft draft,
        Action updatePickerPreviews)
    {
        if (paths.Count == 1)
        {
            AssignSingleDroppedImage(draft, paths[0]);
            updatePickerPreviews();
            return;
        }

        var groups = paths
            .Chunk(2)
            .Where(pair => pair.Length == 2)
            .Select(pair => pair.ToArray())
            .ToArray();
        var hasUnpairedImage = paths.Count % 2 != 0;
        if (groups.Length == 0)
        {
            AssignSingleDroppedImage(draft, paths[0]);
            updatePickerPreviews();
            return;
        }

        if (groups.Length > 1)
        {
            AssignDroppedPair(draft, groups[0]);
            _batchDrafts = [draft];
            _batchDrafts.AddRange(groups.Skip(1)
                .Select(pair => new MapDraft { FloorOnePath = pair[0], FloorTwoPath = pair[1] }));
            _batchDraftIndex = 0;
            _draft = _batchDrafts[0];
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            _pendingMarker = null;
            _dragStart = null;
            await ShowImportAsync(_draft);
            if (hasUnpairedImage)
            {
                await ShowMessageAsync("有一张图片未导入", "批量导入按文件名从小到大排序，每两张图片组成一组；最后一张未配对图片已跳过。");
            }
            return;
        }

        AssignDroppedPair(draft, groups[0]);
        updatePickerPreviews();
        if (hasUnpairedImage)
        {
            await ShowMessageAsync("有一张图片未导入", "批量导入按文件名从小到大排序，每两张图片组成一组；最后一张未配对图片已跳过。");
        }
    }

    private static void AssignSingleDroppedImage(MapDraft draft, string path)
    {
        if (!MapRepository.IsSupportedImage(draft.FloorOnePath) || !File.Exists(draft.FloorOnePath))
        {
            draft.FloorOnePath = path;
            ClearFloorAnchors(draft, "1f");
            return;
        }
        if (!MapRepository.IsSupportedImage(draft.FloorTwoPath) || !File.Exists(draft.FloorTwoPath))
        {
            draft.FloorTwoPath = path;
            ClearFloorAnchors(draft, "2f");
            return;
        }

        draft.FloorOnePath = path;
        ClearFloorAnchors(draft, "1f");
    }

    private static void AssignDroppedPair(MapDraft draft, IReadOnlyList<string> pair)
    {
        draft.FloorOnePath = pair[0];
        draft.FloorTwoPath = pair[1];
        ClearFloorAnchors(draft, "1f");
        ClearFloorAnchors(draft, "2f");
    }

    private bool IsBatchImport => _batchDrafts is { Count: > 1 };

    private void ResetBatchImport()
    {
        _batchDrafts = null;
        _batchDraftIndex = 0;
        _pendingImportFloors = null;
    }

    private void ResetMarkerEditorSession()
    {
        DetachMarkerHostScroller();
        _activeAnchorId = null;
        _isSelectingRecognitionRegion = false;
        _isAnnotationPanelOpen = false;
        _activeAnnotationType = default;
        _pendingMarker = null;
        _dragStart = null;
        _panelDragStart = null;
        _panelPositionRatio = new Point(1d, 0d);
    }

    private bool TryAdvanceBatch()
    {
        if (!IsBatchImport || _batchDrafts is null || _batchDraftIndex + 1 >= _batchDrafts.Count)
            return false;

        _batchDraftIndex++;
        _draft = _batchDrafts[_batchDraftIndex];
        _activeFloorKey = "1f";
        _activeAnchorId = null;
        _isSelectingRecognitionRegion = false;
        _pendingMarker = null;
        _dragStart = null;
        return true;
    }

    private void ShowMarkerEditor()
    {
        if (_draft is null || !HasAnyFloorImage(_draft))
            return;
        DetachMarkerHostScroller();
        _draft.Recognition.EnsureStandardAnchors();
        // Rebuilding the editor refreshes the image after a floor click. Keep
        // the selected floor instead of resetting it to the first floor.
        if (!_draft.Floors.Any(floor => floor.Key == _activeFloorKey))
            _activeFloorKey = _draft.Floors.Count > 0 ? _draft.Floors[0].Key : "1f";
        if (GetActiveAnchor() is null)
            _activeAnchorId = null;

        var root = new Grid { Margin = new Thickness(36, 31, 36, 38), MinHeight = 630 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        titleBlock.Children.Add(CreateTitle("特征标记"));
        titleBlock.Children.Add(CreateDescription("第一张图片完成大门和侧门标记后即可确认；其他楼层标记均为可选。"));
        if (IsBatchImport)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量标注：第 {_batchDraftIndex + 1} / {_batchDrafts!.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        else if (IsBatchOperation && _batchQueue is not null)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量{(_batchType == BatchOperationType.Edit ? "编辑" : "导入")}：第 {_batchQueueIndex + 1} / {_batchQueue.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        header.Children.Add(titleBlock);
        root.Children.Add(header);

        var editor = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        _markerSurface = new Grid
        {
            Height = 540,
            Background = new SolidColorBrush(Color.FromArgb(255, 23, 30, 39)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var imagePath = GetActiveFloorImagePath();
        var bitmap = CreateBitmap(imagePath!);
        bitmap.ImageOpened += (_, _) =>
        {
            if (bitmap.PixelHeight > 0)
                _imageAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            UpdateMarkerSurfaceHeight();
            RenderMarkerVisuals();
        };
        _markerSurface.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        _markerCanvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        _markerSurface.Children.Add(_markerCanvas);
        _markerPanelCanvas = new Canvas();
        _markerControlPanel = CreateMarkerControlPanel();
        _markerControlPanel.SizeChanged += (_, _) => PositionMarkerControlPanel();
        _markerPanelCanvas.Children.Add(_markerControlPanel);
        _markerSurface.Children.Add(_markerPanelCanvas);
        _markerSurface.SizeChanged += (_, _) =>
        {
            UpdateMarkerSurfaceHeight();
            RenderMarkerVisuals();
            PositionMarkerControlPanel();
        };
        _markerSurface.Loaded += (_, _) =>
        {
            AttachMarkerHostScroller();
            PositionMarkerControlPanel();
        };
        _markerSurface.PointerPressed += MarkerSurface_PointerPressed;
        _markerSurface.PointerMoved += MarkerSurface_PointerMoved;
        _markerSurface.PointerReleased += MarkerSurface_PointerReleased;
        _markerSurface.PointerCanceled += MarkerSurface_PointerCanceled;
        editor.Children.Add(_markerSurface);
        Grid.SetRow(editor, 1);
        root.Children.Add(editor);

        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        RenderMarkerVisuals();
        UpdateMarkerConfirmState();
        DispatcherQueue.TryEnqueue(() =>
        {
            AttachMarkerHostScroller();
            PositionMarkerControlPanel();
        });
    }

    private Border CreateMarkerControlPanel()
    {
        var panelLayout = new Grid();
        panelLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Spacing = 5
        };
        var dragHandle = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
            Padding = new Thickness(2, 4, 2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = "楼层",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 224, 230)),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        dragHandle.PointerPressed += MarkerPanelDragHandle_PointerPressed;
        dragHandle.PointerMoved += MarkerPanelDragHandle_PointerMoved;
        dragHandle.PointerReleased += MarkerPanelDragHandle_PointerReleased;
        dragHandle.PointerCanceled += MarkerPanelDragHandle_PointerCanceled;
        header.Children.Add(dragHandle);

        // 动态生成楼层切换按钮（从 draft.Floors）
        // Put floor buttons in a separate grid so additional floors wrap to
        // the next row instead of being squeezed into the header.
        var floorDefinitions = (_draft?.Floors ?? [])
            .OrderBy(floor => floor.SortOrder)
            .ToArray();
        if (floorDefinitions.Length > 0)
        {
            const int buttonsPerRow = 3;
            var floorGrid = new Grid
            {
                ColumnSpacing = 4,
                RowSpacing = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            for (var column = 0; column < buttonsPerRow; column++)
                floorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < (floorDefinitions.Length + buttonsPerRow - 1) / buttonsPerRow; row++)
                floorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var index = 0; index < floorDefinitions.Length; index++)
            {
                var floorDef = floorDefinitions[index];
                var label = floorDef.DisplayName.Length > 4
                    ? floorDef.DisplayName[..4]
                    : floorDef.DisplayName;
                var floorButton = CreateFloorButton(label, floorDef.Key);
                Grid.SetColumn(floorButton, index % buttonsPerRow);
                Grid.SetRow(floorButton, index / buttonsPerRow);
                floorGrid.Children.Add(floorButton);
            }
            header.Children.Add(floorGrid);
        }
        panelLayout.Children.Add(header);

        var controls = new StackPanel { Spacing = 7 };

        // 添加标记 切换按钮
        var addMarkerToggle = CreateMarkerPanelButton("添加标记",
            _isAnnotationPanelOpen ? AccentBlue : Color.FromArgb(255, 100, 180, 100));
        if (_isAnnotationPanelOpen)
        {
            addMarkerToggle.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            addMarkerToggle.BorderThickness = new Thickness(2);
        }
        addMarkerToggle.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(addMarkerToggle);
            _isAnnotationPanelOpen = !_isAnnotationPanelOpen;
            _activeAnnotationType = default;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        controls.Children.Add(addMarkerToggle);

        if (_isAnnotationPanelOpen)
        {
            controls.Children.Add(CreateAnnotationSubPanel());
        }

        var regionButton = CreateMarkerPanelButton("区域选择", RecognitionRegionOrange);
        if (_isSelectingRecognitionRegion)
        {
            regionButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            regionButton.BorderThickness = new Thickness(2);
        }
        regionButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(regionButton);
            _isSelectingRecognitionRegion = true;
            _activeAnchorId = null;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        controls.Children.Add(regionButton);
        var wholeRegionButton = CreateMarkerPanelButton("整图作为区域", Color.FromArgb(255, 112, 112, 112));
        wholeRegionButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(wholeRegionButton);
            ApplyRecognitionRegion(new NormalizedRectangle { Width = 1d, Height = 1d });
            _isSelectingRecognitionRegion = false;
            _activeAnchorId = null;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        controls.Children.Add(wholeRegionButton);

        foreach (var requiredAnchor in GetActiveFloorProfile().RequiredAnchors)
            controls.Children.Add(CreateAnchorButton(requiredAnchor));

        var addOptionalButton = CreateMarkerPanelButton("+ 辅助锚点", Color.FromArgb(255, 247, 184, 24));
        addOptionalButton.Click += (_, _) => AddOptionalAnchor();
        controls.Children.Add(addOptionalButton);

        foreach (var optionalAnchor in GetActiveFloorProfile().Anchors
                     .Where(anchor => anchor.Role == RecognitionAnchorRole.Optional))
        {
            if (optionalAnchor.IsBuiltIn)
            {
                controls.Children.Add(CreateAnchorButton(optionalAnchor));
                continue;
            }
            var optionalRow = new Grid { ColumnSpacing = 6 };
            optionalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optionalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var anchorButton = CreateAnchorButton(optionalAnchor);
            optionalRow.Children.Add(anchorButton);
            var deleteButton = CreateMarkerPanelButton("X", Color.FromArgb(255, 255, 90, 66));
            deleteButton.Padding = new Thickness(0);
            deleteButton.Click += (_, _) => DeleteOptionalAnchor(optionalAnchor.Id);
            Grid.SetColumn(deleteButton, 1);
            optionalRow.Children.Add(deleteButton);
            controls.Children.Add(optionalRow);
        }

        var exitButton = CreateMarkerPanelButton("退出", Color.FromArgb(255, 112, 112, 112));
        exitButton.Click += async (_, _) =>
        {
            ResetBatchOperation();
            await ShowListAsync();
        };
        controls.Children.Add(exitButton);

        _markerConfirmButton = CreateMarkerPanelButton("确认", DisabledGray);
        _markerConfirmButton.Click += async (_, _) =>
        {
            PlayDetailTriggerFeedback(_markerConfirmButton);
            await SaveDraftAsync();
        };
        controls.Children.Add(_markerConfirmButton);

        var scroller = new ScrollViewer
        {
            Content = controls,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 1);
        panelLayout.Children.Add(scroller);
        var panel = new Border
        {
            Width = MarkerPanelPreferredWidth,
            MinWidth = 0,
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromArgb(218, 16, 24, 34)),
            CornerRadius = new CornerRadius(10),
            Child = panelLayout
        };
        panel.PointerPressed += (_, e) => e.Handled = true;
        return panel;
    }

    private void RefreshMarkerControlPanel()
    {
        if (_markerPanelCanvas is null)
            return;
        _markerPanelCanvas.Children.Clear();
        _markerControlPanel = CreateMarkerControlPanel();
        _markerControlPanel.SizeChanged += (_, _) => PositionMarkerControlPanel();
        _markerPanelCanvas.Children.Add(_markerControlPanel);
        UpdateMarkerConfirmState();
        DispatcherQueue.TryEnqueue(PositionMarkerControlPanel);
    }

    private Button CreateFloorButton(string label, string floorKey)
    {
        var button = CreateMarkerPanelButton(label, Color.FromArgb(255, 129, 129, 129));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(0);
        if (_activeFloorKey == floorKey)
        {
            button.Background = new SolidColorBrush(AccentBlue);
            button.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            button.BorderBrush = new SolidColorBrush(AccentBlue);
        }
        button.Click += (_, _) =>
        {
            if (_activeFloorKey == floorKey)
                return;
            _activeFloorKey = floorKey;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            _activeAnnotationType = default;
            _pendingMarker = null;
            _dragStart = null;
            ShowMarkerEditor();
        };
        return button;
    }

    private Button CreateAnchorButton(RecognitionAnchor anchor)
    {
        var button = CreateMarkerPanelButton(anchor.DisplayName, GetAnchorColor(anchor));
        if (_activeAnchorId == anchor.Id)
        {
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            button.BorderThickness = new Thickness(2);
        }
        button.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(button);
            _activeAnchorId = anchor.Id;
            _isSelectingRecognitionRegion = false;
            _pendingMarker = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        return button;
    }

    private static Button CreateMarkerPanelButton(string text, Color color)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(color),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            MinWidth = 0,
            MinHeight = 28,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AttachHoverFeedback(button);
        return button;
    }

    private void AddOptionalAnchor()
    {
        if (_draft is null)
            return;
        var profile = GetActiveFloorProfile();
        var number = profile.Anchors.Count(anchor => anchor.Role == RecognitionAnchorRole.Optional) + 1;
        var anchor = new RecognitionAnchor
        {
            Key = $"optional-{Guid.NewGuid():N}",
            DisplayName = $"辅助锚点 {number}",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.35d
        };
        profile.Anchors.Add(anchor);
        _activeAnchorId = anchor.Id;
        _isSelectingRecognitionRegion = false;
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

    private void DeleteOptionalAnchor(Guid anchorId)
    {
        var anchor = GetActiveFloorProfile().FindAnchor(anchorId);
        if (anchor?.Role != RecognitionAnchorRole.Optional || anchor.IsBuiltIn)
            return;
        GetActiveFloorProfile().Anchors.Remove(anchor);
        if (_activeAnchorId == anchor.Id)
            _activeAnchorId = null;
        _pendingMarker = null;
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

    private Border CreateAnnotationSubPanel()
    {
        var panelLayout = new StackPanel { Spacing = 7 };
        var colorLabel = new TextBlock
        {
            Text = "标记颜色",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 184, 190))
        };
        panelLayout.Children.Add(colorLabel);

        var colorGrid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < 3; i++)
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        for (var i = 0; i < 9; i++)
        {
            var colorIndex = i;
            var swatch = new Button
            {
                Background = new SolidColorBrush(AnnotationColors[i]),
                BorderBrush = new SolidColorBrush(_selectedAnnotationColor == i
                    ? Color.FromArgb(255, 255, 255, 255)
                    : AnnotationColors[i]),
                BorderThickness = new Thickness(_selectedAnnotationColor == i ? 2 : 1),
                Width = 28,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            swatch.Click += (_, _) =>
            {
                _selectedAnnotationColor = colorIndex;
                RefreshMarkerControlPanel();
            };
            Grid.SetRow(swatch, i / 3);
            Grid.SetColumn(swatch, i % 3);
            colorGrid.Children.Add(swatch);
        }
        panelLayout.Children.Add(colorGrid);

        var textButtonColor = AnnotationColors[_selectedAnnotationColor];
        var textButton = CreateMarkerPanelButton("注释文字", textButtonColor);
        if (_selectedAnnotationColor == 8)
            textButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        if (_activeAnnotationType == MapAnnotationType.Text)
        {
            textButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            textButton.BorderThickness = new Thickness(2);
        }
        textButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(textButton);
            _activeAnnotationType = MapAnnotationType.Text;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        panelLayout.Children.Add(textButton);

        var boxButton = CreateMarkerPanelButton("标注框线", AnnotationColors[_selectedAnnotationColor]);
        if (_selectedAnnotationColor == 8)
            boxButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        if (_activeAnnotationType == MapAnnotationType.Outline)
        {
            boxButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            boxButton.BorderThickness = new Thickness(2);
        }
        boxButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(boxButton);
            _activeAnnotationType = MapAnnotationType.Outline;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        panelLayout.Children.Add(boxButton);

        var activeAnnotations = GetActiveFloorProfile().Annotations.ToList();
        for (var i = 0; i < activeAnnotations.Count; i++)
        {
            var annotation = activeAnnotations[i];
            var number = i + 1;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var label = annotation.Type == MapAnnotationType.Text
                ? (string.IsNullOrWhiteSpace(annotation.Text) ? $"文字 {number}" : annotation.Text)
                : $"框线 {number}";
            var labelText = new TextBlock
            {
                Text = label.Length > 8 ? label[..8] : label,
                FontSize = 11,
                Foreground = new SolidColorBrush(AnnotationColors[annotation.ColorIndex]),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(labelText);

            var capturedId = annotation.Id;
            var deleteButton = CreateMarkerPanelButton("X", Color.FromArgb(255, 255, 90, 66));
            deleteButton.Padding = new Thickness(0);
            deleteButton.Click += (_, _) => DeleteAnnotation(capturedId);
            Grid.SetColumn(deleteButton, 1);
            row.Children.Add(deleteButton);

            panelLayout.Children.Add(row);
        }

        return new Border
        {
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(140, 16, 24, 34)),
            CornerRadius = new CornerRadius(8),
            Child = panelLayout
        };
    }

    private void DeleteAnnotation(Guid id)
    {
        var profile = GetActiveFloorProfile();
        profile.Annotations.RemoveAll(a => a.Id == id);
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

    private async Task CommitTextAnnotationAsync(NormalizedRectangle bounds)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "输入注释文字…",
            AcceptsReturn = false,
            Height = 36
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "输入注释文字",
            Content = textBox,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || string.IsNullOrWhiteSpace(textBox.Text))
        {
            _activeAnnotationType = default;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
            return;
        }

        GetActiveFloorProfile().Annotations.Add(new MapAnnotation
        {
            Type = MapAnnotationType.Text,
            ColorIndex = _selectedAnnotationColor,
            Bounds = bounds.Clone(),
            Text = textBox.Text.Trim()
        });
        _activeAnnotationType = default;
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

    private void MarkerSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;
        if ((!_isSelectingRecognitionRegion && GetActiveAnchor() is null && !isAnnotationMode) || _markerSurface is null)
            return;
        var surfacePoint = e.GetCurrentPoint(_markerSurface).Position;
        var point = _isSelectingRecognitionRegion || isAnnotationMode
            ? ToSourceNormalizedPoint(surfacePoint)
            : ToRecognitionNormalizedPoint(surfacePoint);
        if (point is null)
            return;

        _dragStart = point;
        _pendingMarker = new NormalizedRectangle { X = point.Value.X, Y = point.Value.Y };
        _markerSurface.CapturePointer(e.Pointer);
        RenderMarkerVisuals();
        e.Handled = true;
    }

    private void MarkerSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || _markerSurface is null)
            return;
        var surfacePoint = e.GetCurrentPoint(_markerSurface).Position;
        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;
        var point = _isSelectingRecognitionRegion || isAnnotationMode
            ? ToSourceNormalizedPoint(surfacePoint, clamp: true)
            : ToRecognitionNormalizedPoint(surfacePoint, clamp: true);
        if (point is null)
            return;

        _pendingMarker = CreateNormalizedRectangle(_dragStart.Value, point.Value);
        RenderMarkerVisuals();
    }

    private void MarkerSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is null || _markerSurface is null)
            return;
        var surfacePoint = e.GetCurrentPoint(_markerSurface).Position;
        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;
        var point = _isSelectingRecognitionRegion || isAnnotationMode
            ? ToSourceNormalizedPoint(surfacePoint, clamp: true)
            : ToRecognitionNormalizedPoint(surfacePoint, clamp: true);
        if (point is not null)
            _pendingMarker = CreateNormalizedRectangle(_dragStart.Value, point.Value);
        _markerSurface.ReleasePointerCapture(e.Pointer);
        CommitPendingMarker();
    }

    private void MarkerSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_markerSurface is not null)
            _markerSurface.ReleasePointerCapture(e.Pointer);
        _pendingMarker = null;
        _dragStart = null;
        RenderMarkerVisuals();
    }

    private void CommitPendingMarker()
    {
        if (_activeAnnotationType == MapAnnotationType.Outline
            && _pendingMarker?.IsValid is true)
        {
            GetActiveFloorProfile().Annotations.Add(new MapAnnotation
            {
                Type = MapAnnotationType.Outline,
                ColorIndex = _selectedAnnotationColor,
                Bounds = _pendingMarker.Clone()
            });
            _pendingMarker = null;
            _dragStart = null;
            _activeAnnotationType = default;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
            return;
        }

        if (_activeAnnotationType == MapAnnotationType.Text
            && _pendingMarker?.IsValid is true)
        {
            var bounds = _pendingMarker;
            _pendingMarker = null;
            _dragStart = null;
            _ = CommitTextAnnotationAsync(bounds);
            return;
        }

        if (_isSelectingRecognitionRegion)
        {
            if (_pendingMarker?.IsValid is true)
                ApplyRecognitionRegion(_pendingMarker);
        }
        else
        {
            var anchor = GetActiveAnchor();
            if (anchor is not null && _pendingMarker?.IsValid is true)
                anchor.Bounds = _pendingMarker.Clone();
        }
        _pendingMarker = null;
        _dragStart = null;
        UpdateMarkerConfirmState();
        RenderMarkerVisuals();
    }

    private void ApplyRecognitionRegion(NormalizedRectangle newRegion)
    {
        var profile = GetActiveFloorProfile();
        MapRecognitionCoordinates.ApplyRecognitionRegion(profile, newRegion);
    }

    private void RenderMarkerVisuals()
    {
        if (_markerCanvas is null || _markerSurface is null || _draft is null)
            return;
        _markerCanvas.Children.Clear();
        if (_markerSurface.ActualWidth <= 0 || _markerSurface.ActualHeight <= 0)
            return;

        var isAnnotationMode = _activeAnnotationType is MapAnnotationType.Text or MapAnnotationType.Outline;

        if (!_isSelectingRecognitionRegion && !isAnnotationMode && GetActiveAnchor() is not null)
        {
            _markerCanvas.Children.Add(new Rectangle
            {
                Width = _markerSurface.ActualWidth,
                Height = _markerSurface.ActualHeight,
                Fill = new SolidColorBrush(Color.FromArgb(168, 0, 0, 0)),
                IsHitTestVisible = false
            });
        }

        // Dim the surface when dragging in annotation mode
        if (isAnnotationMode && _dragStart is not null)
        {
            _markerCanvas.Children.Add(new Rectangle
            {
                Width = _markerSurface.ActualWidth,
                Height = _markerSurface.ActualHeight,
                Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                IsHitTestVisible = false
            });
        }

        // Render annotation pending marker during drag
        if (isAnnotationMode && _pendingMarker?.IsValid is true)
        {
            AddMarkerRectangle(_pendingMarker, AnnotationColors[_selectedAnnotationColor],
                isSourceRelative: true, isDashed: _activeAnnotationType == MapAnnotationType.Text);
        }

        var displayedRegion = _isSelectingRecognitionRegion && _pendingMarker?.IsValid is true
            ? _pendingMarker
            : GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        AddMarkerRectangle(displayedRegion, RecognitionRegionRed, isSourceRelative: true, isDashed: true);

        var recognitionRegion = GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        foreach (var anchor in GetActiveFloorProfile().Anchors)
        {
            var bounds = anchor.Id == _activeAnchorId && _pendingMarker is not null
                ? _pendingMarker
                : anchor.Bounds;
            AddMarkerRectangle(bounds, GetAnchorColor(anchor), isSourceRelative: false, isDashed: false, recognitionRegion);
        }

        foreach (var annotation in GetActiveFloorProfile().Annotations)
        {
            if (!annotation.IsValid)
                continue;
            var color = AnnotationColors[annotation.ColorIndex];
            var isDashed = annotation.Type == MapAnnotationType.Text;
            AddMarkerRectangle(annotation.Bounds, color, isSourceRelative: true, isDashed: isDashed);
            if (annotation.Type == MapAnnotationType.Text && !string.IsNullOrWhiteSpace(annotation.Text))
            {
                AddAnnotationTextLabel(annotation.Bounds, annotation.Text, color);
            }
        }
    }

    private void AddMarkerRectangle(
        NormalizedRectangle? marker,
        Color color,
        bool isSourceRelative,
        bool isDashed,
        NormalizedRectangle? recognitionRegion = null)
    {
        if (marker?.IsValid is not true || _markerCanvas is null)
            return;
        var visible = GetVisibleImageBounds();
        var sourceMarker = isSourceRelative
            ? marker
            : ToSourceRectangle(marker, recognitionRegion ?? GetActiveFloorProfile().GetEffectiveRecognitionRegion());
        var thickness = isDashed ? 3d : 5d;
        var left = visible.X + sourceMarker.X * visible.Width;
        var top = visible.Y + sourceMarker.Y * visible.Height;
        var width = sourceMarker.Width * visible.Width;
        var height = sourceMarker.Height * visible.Height;
        if (isDashed)
        {
            var halfStroke = thickness / 2d;
            if (sourceMarker.X <= 0.000001d)
            {
                left += halfStroke;
                width -= halfStroke;
            }
            if (sourceMarker.Y <= 0.000001d)
            {
                top += halfStroke;
                height -= halfStroke;
            }
            if (sourceMarker.X + sourceMarker.Width >= 0.999999d)
                width -= halfStroke;
            if (sourceMarker.Y + sourceMarker.Height >= 0.999999d)
                height -= halfStroke;
        }
        var rectangle = new Rectangle
        {
            Width = Math.Max(0d, width),
            Height = Math.Max(0d, height),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            IsHitTestVisible = false
        };
        if (isDashed)
            rectangle.StrokeDashArray = new DoubleCollection { 7d, 5d };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        _markerCanvas.Children.Add(rectangle);
    }

    private void AddAnnotationTextLabel(NormalizedRectangle bounds, string text, Color color)
    {
        if (_markerCanvas is null || _markerSurface is null || string.IsNullOrWhiteSpace(text))
            return;
        var visible = GetVisibleImageBounds();
        if (visible.Width <= 0 || visible.Height <= 0)
            return;
        var left = visible.X + bounds.X * visible.Width;
        var top = visible.Y + bounds.Y * visible.Height;
        var width = bounds.Width * visible.Width;
        var height = bounds.Height * visible.Height;
        if (width <= 0 || height <= 0)
            return;

        var label = new TextBlock
        {
            Text = text,
            FontSize = CalculateFittingFontSize(text, width, height),
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = width,
            Height = height,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        _markerCanvas.Children.Add(label);
    }

    /// <summary>
    /// Picks the largest font size that keeps <paramref name="text"/> inside the given
    /// pixel width and height.  CJK characters are roughly square, so we budget ~0.85
    /// of the character height per em and scale down when the text would overflow
    /// horizontally.
    /// </summary>
    private static double CalculateFittingFontSize(string text, double pixelWidth, double pixelHeight)
    {
        if (string.IsNullOrEmpty(text) || pixelWidth <= 0 || pixelHeight <= 0)
            return 8;

        var maxByHeight = pixelHeight * 0.85;
        // Each CJK character is assumed to occupy ~0.82em in width at the chosen size.
        var maxByWidth = pixelWidth / (text.Length * 0.82);
        var fontSize = Math.Min(maxByHeight, maxByWidth);
        return Math.Clamp(fontSize, 8, Math.Min(pixelHeight, 48));
    }

    private Point? ToSourceNormalizedPoint(Point point, bool clamp = false) =>
        ToNormalizedPoint(point, GetVisibleImageBounds(), clamp);

    private Point? ToRecognitionNormalizedPoint(Point point, bool clamp = false) =>
        ToNormalizedPoint(point, GetVisibleRecognitionBounds(), clamp);

    private static Point? ToNormalizedPoint(Point point, Rect bounds, bool clamp)
    {
        if (bounds.Width <= 0d || bounds.Height <= 0d)
            return null;
        if (!clamp
            && (point.X < bounds.X || point.Y < bounds.Y
                || point.X > bounds.X + bounds.Width || point.Y > bounds.Y + bounds.Height))
            return null;
        return new Point(
            Math.Clamp((point.X - bounds.X) / bounds.Width, 0, 1),
            Math.Clamp((point.Y - bounds.Y) / bounds.Height, 0, 1));
    }

    private Rect GetVisibleRecognitionBounds()
    {
        var visible = GetVisibleImageBounds();
        var region = GetActiveFloorProfile().GetEffectiveRecognitionRegion();
        return new Rect(
            visible.X + region.X * visible.Width,
            visible.Y + region.Y * visible.Height,
            region.Width * visible.Width,
            region.Height * visible.Height);
    }

    private Rect GetVisibleImageBounds()
    {
        if (_markerSurface is null || _markerSurface.ActualWidth <= 0 || _markerSurface.ActualHeight <= 0)
            return Rect.Empty;
        var surfaceRatio = _markerSurface.ActualWidth / _markerSurface.ActualHeight;
        if (surfaceRatio > _imageAspectRatio)
        {
            var height = _markerSurface.ActualHeight;
            var width = height * _imageAspectRatio;
            return new Rect((_markerSurface.ActualWidth - width) / 2, 0, width, height);
        }

        var imageWidth = _markerSurface.ActualWidth;
        var imageHeight = imageWidth / _imageAspectRatio;
        return new Rect(0, (_markerSurface.ActualHeight - imageHeight) / 2, imageWidth, imageHeight);
    }

    private void UpdateMarkerSurfaceHeight()
    {
        if (_markerSurface is null || _markerSurface.ActualWidth <= 0 || _imageAspectRatio <= 0)
            return;
        var targetHeight = Math.Round(_markerSurface.ActualWidth / _imageAspectRatio);
        if (Math.Abs(_markerSurface.Height - targetHeight) > 1)
            _markerSurface.Height = targetHeight;
    }

    private void PositionMarkerControlPanel()
    {
        if (_markerControlPanel is null || _markerSurface is null)
            return;
        var visible = GetMarkerPanelBounds();
        if (visible.Width <= 0d || visible.Height <= 0d)
            return;
        var horizontalInset = Math.Min(MarkerPanelInset, visible.Width / 4d);
        var topInset = Math.Min(MarkerPanelTopSafeInset, visible.Height / 3d);
        var bottomInset = Math.Min(MarkerPanelInset, visible.Height / 4d);
        var maximumWidth = Math.Max(1d, visible.Width - (horizontalInset * 2d));
        var maximumHeight = Math.Max(1d, visible.Height - topInset - bottomInset);
        var targetWidth = Math.Min(MarkerPanelPreferredWidth, maximumWidth);
        if (Math.Abs(_markerControlPanel.Width - targetWidth) > 0.5d)
            _markerControlPanel.Width = targetWidth;
        _markerControlPanel.MaxHeight = maximumHeight;

        var panelWidth = Math.Min(
            _markerControlPanel.ActualWidth > 0d ? _markerControlPanel.ActualWidth : targetWidth,
            maximumWidth);
        var panelHeight = Math.Min(
            _markerControlPanel.ActualHeight > 0d ? _markerControlPanel.ActualHeight : maximumHeight,
            maximumHeight);
        var leftMinimum = visible.X + horizontalInset;
        var topMinimum = visible.Y + topInset;
        var horizontalTravel = Math.Max(0d, maximumWidth - panelWidth);
        var verticalTravel = Math.Max(0d, maximumHeight - panelHeight);
        Canvas.SetLeft(
            _markerControlPanel,
            leftMinimum + Math.Clamp(_panelPositionRatio.X, 0d, 1d) * horizontalTravel);
        Canvas.SetTop(
            _markerControlPanel,
            topMinimum + Math.Clamp(_panelPositionRatio.Y, 0d, 1d) * verticalTravel);
    }

    private void SetMarkerControlPanelPosition(Point requestedPosition)
    {
        if (_markerControlPanel is null)
            return;
        var visible = GetMarkerPanelBounds();
        if (visible.Width <= 0d || visible.Height <= 0d)
            return;
        var horizontalInset = Math.Min(MarkerPanelInset, visible.Width / 4d);
        var topInset = Math.Min(MarkerPanelTopSafeInset, visible.Height / 3d);
        var bottomInset = Math.Min(MarkerPanelInset, visible.Height / 4d);
        var maximumWidth = Math.Max(1d, visible.Width - (horizontalInset * 2d));
        var maximumHeight = Math.Max(1d, visible.Height - topInset - bottomInset);
        var panelWidth = Math.Min(_markerControlPanel.ActualWidth, maximumWidth);
        var panelHeight = Math.Min(_markerControlPanel.ActualHeight, maximumHeight);
        var leftMinimum = visible.X + horizontalInset;
        var topMinimum = visible.Y + topInset;
        var horizontalTravel = Math.Max(0d, maximumWidth - panelWidth);
        var verticalTravel = Math.Max(0d, maximumHeight - panelHeight);
        var left = Math.Clamp(requestedPosition.X, leftMinimum, leftMinimum + horizontalTravel);
        var top = Math.Clamp(requestedPosition.Y, topMinimum, topMinimum + verticalTravel);
        Canvas.SetLeft(_markerControlPanel, left);
        Canvas.SetTop(_markerControlPanel, top);
        _panelPositionRatio = new Point(
            horizontalTravel > 0d ? (left - leftMinimum) / horizontalTravel : 0d,
            verticalTravel > 0d ? (top - topMinimum) / verticalTravel : 0d);
    }

    private void MarkerPanelDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_markerSurface is null || _markerControlPanel is null || sender is not UIElement handle)
            return;
        PositionMarkerControlPanel();
        _panelDragStart = e.GetCurrentPoint(_markerSurface).Position;
        _panelDragOrigin = new Point(
            Canvas.GetLeft(_markerControlPanel),
            Canvas.GetTop(_markerControlPanel));
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MarkerPanelDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_panelDragStart is null || _markerSurface is null)
            return;
        var current = e.GetCurrentPoint(_markerSurface).Position;
        SetMarkerControlPanelPosition(new Point(
            _panelDragOrigin.X + current.X - _panelDragStart.Value.X,
            _panelDragOrigin.Y + current.Y - _panelDragStart.Value.Y));
        e.Handled = true;
    }

    private void MarkerPanelDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        _panelDragStart = null;
        e.Handled = true;
    }

    private void MarkerPanelDragHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement handle)
            handle.ReleasePointerCapture(e.Pointer);
        _panelDragStart = null;
        PositionMarkerControlPanel();
        e.Handled = true;
    }

    private Rect GetMarkerPanelBounds()
    {
        var imageBounds = GetVisibleImageBounds();
        if (imageBounds.Width <= 0d || imageBounds.Height <= 0d
            || _markerPanelCanvas is null
            || _markerHostScroller is null
            || _markerHostScroller.ActualWidth <= 0d
            || _markerHostScroller.ActualHeight <= 0d)
        {
            return imageBounds;
        }

        try
        {
            var viewportTransform = _markerHostScroller.TransformToVisual(_markerPanelCanvas);
            var topLeft = viewportTransform.TransformPoint(new Point(0d, 0d));
            var bottomRight = viewportTransform.TransformPoint(
                new Point(_markerHostScroller.ActualWidth, _markerHostScroller.ActualHeight));
            var viewportBounds = new Rect(
                Math.Min(topLeft.X, bottomRight.X),
                Math.Min(topLeft.Y, bottomRight.Y),
                Math.Abs(bottomRight.X - topLeft.X),
                Math.Abs(bottomRight.Y - topLeft.Y));
            return Intersect(imageBounds, viewportBounds);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return imageBounds;
        }
    }

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

    private void UpdateMarkerConfirmState()
    {
        if (_markerConfirmButton is null || _draft is null)
            return;
        var canConfirm = _draft.Recognition.HasFirstFloorGateMarkers();
        _markerConfirmButton.IsEnabled = canConfirm;
        _markerConfirmButton.Background = new SolidColorBrush(canConfirm ? AccentBlue : DisabledGray);
    }

    private async Task SaveDraftAsync()
    {
        if (_draft is null)
            return;
        try
        {
            var savedMap = await _repository.SaveAsync(_draft);
            _selectedMapIds.Add(savedMap.Id);

            // Old batch import flow (multiple image pairs dropped at once)
            if (TryAdvanceBatch())
            {
                ShowMarkerEditor();
                return;
            }

            // New multi-select batch operation flow
            if (TryAdvanceBatchQueue() && _batchQueue is not null)
            {
                var nextMap = _batchQueue[_batchQueueIndex];
                if (_batchType == BatchOperationType.Edit)
                    await EditMapAsync(nextMap);
                else if (_batchType == BatchOperationType.Import)
                    await ImportMapAsync(nextMap);
                return;
            }

            await MapRuntimeHost.Current.RefreshMapCacheAsync(savedMap.Id);
            _draft = null;
            _activeAnchorId = null;
            ResetBatchImport();
            ResetBatchOperation();
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("保存失败", exception.Message);
        }
    }

    private TeachingTip CreateImportTeachingTip(Button importButton, Button exportButton)
    {
        var createMap = new Button
        {
            Content = "创建地图",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 150
        };
        var importPackage = new Button
        {
            Content = "导入数据包",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 150
        };
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(createMap);
        choices.Children.Add(importPackage);
        var tip = new TeachingTip
        {
            Target = importButton,
            Title = "请选择导入方式",
            Subtitle = "创建新地图，或导入其他用户分享的 IDVM 数据包。",
            Content = choices,
            IsLightDismissEnabled = true,
            PreferredPlacement = TeachingTipPlacementMode.Bottom
        };
        createMap.Click += async (_, _) =>
        {
            tip.IsOpen = false;
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            await ShowImportAsync(new MapDraft { Class = _selectedClass });
        };
        importPackage.Click += async (_, _) =>
        {
            tip.IsOpen = false;
            await ImportIdvmPackageAsync(importButton, exportButton);
        };
        return tip;
    }

    private async Task ImportIdvmPackageAsync(Button importButton, Button exportButton)
    {
        var packagePath = await PickIdvmPackageAsync();
        if (packagePath is null)
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在导入…");
        IdvmImportPlan? plan = null;
        try
        {
            plan = await Task.Run(() => _idvmPackageService.InspectAsync(packagePath));
            var result = await _idvmPackageService.ImportAsync(plan);
            plan = null; // ImportAsync owns disposal after this point.
            try
            {
                await MapRuntimeHost.Current.RefreshMapCacheAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Imported maps but runtime refresh failed: {exception}");
            }

            // ShowListAsync rebuilds the action row. Clear the operation state
            // before rebuilding so the newly-created import/export buttons are
            // initialized with the completed state rather than inheriting the
            // old buttons' busy state.
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
            _selectedClass = result.CreatedClasses[0];
            _selectedMapIds.Clear();
            _lastClickedMapId = null;
            await ShowListAsync();
            await ShowMessageAsync(
                "数据包导入完成",
                $"已创建 {result.CreatedClasses.Count} 个 Class，导入 {result.ImportedMaps.Count} 张地图。\n"
                + string.Join("、", result.CreatedClasses));
        }
        catch (Exception exception)
        {
            if (plan is not null)
                await plan.DisposeAsync();
            await ShowMessageAsync("数据包导入失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private async Task ShowExportDialogAsync(Button importButton, Button exportButton)
    {
        var currentCount = GetVisibleMaps().Count;
        var totalCount = _loadedMaps.Count;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"当前 Class：{_selectedClass}（{currentCount} 张地图）",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"全部非空 Class：{_classes.Count(name => _loadedMaps.Any(map => string.Equals(map.Class, name, StringComparison.OrdinalIgnoreCase)))} 个，{totalCount} 张地图",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出 IDVM 数据包",
            Content = content,
            PrimaryButtonText = "当前 Class",
            SecondaryButtonText = "全部地图",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = currentCount > 0,
            IsSecondaryButtonEnabled = totalCount > 0,
            DefaultButton = currentCount > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Secondary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return;
        var scope = result == ContentDialogResult.Primary
            ? IdvmExportScope.CurrentClass
            : IdvmExportScope.AllClasses;
        var suggestedName = scope == IdvmExportScope.CurrentClass
            ? $"IDVB-{SanitizeFileName(_selectedClass)}-{DateTime.Now:yyyyMMdd-HHmmss}"
            : $"IDVB-All-{DateTime.Now:yyyyMMdd-HHmmss}";
        var destination = await PickIdvmDestinationAsync(suggestedName);
        if (destination is null)
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在导出…");
        try
        {
            await Task.Run(() => _idvmPackageService.ExportAsync(
                scope,
                scope == IdvmExportScope.CurrentClass ? _selectedClass : null,
                destination));
            await ShowMessageAsync("数据包导出完成", $"已保存到：\n{destination}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("数据包导出失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private void SetPackageOperationState(
        Button importButton,
        Button exportButton,
        bool isBusy,
        string? busyText)
    {
        _isPackageOperation = isBusy;
        importButton.IsEnabled = !isBusy;
        exportButton.IsEnabled = !isBusy && _loadedMaps.Count > 0;
        exportButton.Content = isBusy ? busyText : "导出";
    }

    private async Task<string?> PickIdvmPackageAsync()
    {
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "导入",
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".idvm");
            var result = await picker.PickSingleFileAsync();
            return result is null || string.IsNullOrWhiteSpace(result.Path)
                ? null
                : result.Path;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件选择器", exception.Message);
            return null;
        }
    }

    private async Task<string?> PickIdvmDestinationAsync(string suggestedName)
    {
        try
        {
            var picker = new FileSavePicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
                DefaultFileExtension = ".idvm",
                CommitButtonText = "导出",
                FileTypeChoices =
                {
                    { "IDVM 地图数据包", new List<string> { ".idvm" } }
                }
            };
            var result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
                return null;
            return System.IO.Path.GetExtension(result.Path).Equals(".idvm", StringComparison.OrdinalIgnoreCase)
                ? result.Path
                : result.Path + ".idvm";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开保存选择器", exception.Message);
            return null;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Class" : sanitized;
    }

    private async Task<string?> PickImageAsync(string title)
    {
        PickFileResult? result;
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "选择",
                ViewMode = PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            result = await picker.PickSingleFileAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件选择器", $"{title}失败：{exception.Message}");
            return null;
        }

        if (result is null)
            return null;

        try
        {
            if (string.IsNullOrWhiteSpace(result.Path) || !File.Exists(result.Path))
                throw new FileNotFoundException("选择的图片不存在。", result.Path);

            var file = await StorageFile.GetFileFromPathAsync(result.Path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            _ = await BitmapDecoder.CreateAsync(stream);
            return result.Path;
        }
        catch
        {
            await ShowMessageAsync("无法读取图片", "请选择有效的 PNG、JPG 或 JPEG 图片。");
            return null;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }

    private FloorRecognitionProfile GetActiveFloorProfile()
    {
        if (_draft is null)
            throw new InvalidOperationException("当前没有可编辑的地图。");
        return _draft.Recognition.GetFloor(_activeFloorKey)
            ?? throw new InvalidOperationException($"不存在的楼层 '{_activeFloorKey}'。");
    }

    private RecognitionAnchor? GetActiveAnchor() =>
        _activeAnchorId is { } id ? GetActiveFloorProfile().FindAnchor(id) : null;

    private string? GetActiveFloorImagePath() => _draft is null
        ? null
        : _draft.FloorPaths.TryGetValue(_activeFloorKey, out var path)
            ? path
            : _activeFloorKey == "1f" ? _draft.FloorOnePath : _draft.FloorTwoPath;

    private static void ClearFloorAnchors(MapDraft draft, string floorKey)
    {
        draft.Recognition.EnsureStandardAnchors();
        var profile = draft.Recognition.GetFloor(floorKey)
            ?? throw new InvalidOperationException($"不存在的楼层 '{floorKey}'。");
        profile.RecognitionRegion = null;
        foreach (var anchor in profile.Anchors)
            anchor.Bounds = null;
    }

    private static bool HasAnyFloorImage(MapDraft draft) =>
        draft.FloorPaths.Count > 0
        && draft.FloorPaths.Values.Any(path => MapRepository.IsSupportedImage(path) && File.Exists(path));

    private static string BuildRecognitionSummary(MapRecord map) =>
        $"一楼：{BuildFloorSummary(map.Recognition.FirstFloor)} · 二楼：{BuildFloorSummary(map.Recognition.SecondFloor)}";

    private static string BuildFloorSummary(FloorRecognitionProfile floor)
    {
        var required = floor.RequiredAnchors.ToArray();
        var markedRequired = required.Count(anchor => anchor.IsMarked);
        var markedOptional = floor.Anchors.Count(anchor =>
            anchor.Role == RecognitionAnchorRole.Optional && anchor.IsMarked);
        return $"{markedRequired}/{required.Length} 必需，{markedOptional} 辅助";
    }

    private static Color GetAnchorColor(RecognitionAnchor anchor) => anchor.Key switch
    {
        "main-entrance" => MainEntranceBlue,
        "side-entrance" => SideEntranceGreen,
        "second-floor-primary" => SecondFloorPurple,
        _ => OptionalAnchorOrange
    };

    private static NormalizedRectangle CreateNormalizedRectangle(Point start, Point end) => new()
    {
        X = Math.Min(start.X, end.X),
        Y = Math.Min(start.Y, end.Y),
        Width = Math.Abs(end.X - start.X),
        Height = Math.Abs(end.Y - start.Y)
    };

    private static NormalizedRectangle ToSourceRectangle(
        NormalizedRectangle regionRelative,
        NormalizedRectangle region) => new()
    {
        X = region.X + regionRelative.X * region.Width,
        Y = region.Y + regionRelative.Y * region.Height,
        Width = regionRelative.Width * region.Width,
        Height = regionRelative.Height * region.Height
    };

    private static TextBlock CreateTitle(string text) => new()
    {
        Text = text,
        FontSize = 29,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Left
    };

    private static TextBlock CreateDescription(string text) => new()
    {
        Text = text,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 828,
        HorizontalAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Left
    };

    private static Button CreateActionButton(string text, Color color)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(color),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            FontSize = 14,
            MinWidth = 130,
            MinHeight = 45,
            Padding = new Thickness(25, 7, 25, 7),
            CornerRadius = new CornerRadius(8)
        };
        AttachHoverFeedback(button);
        return button;
    }

    private static Button CreateSecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(Color.FromArgb(255, 242, 242, 242)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 43, 43, 43)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 218, 218, 218)),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            MinWidth = 98,
            MinHeight = 38,
            Padding = new Thickness(16, 6, 16, 6),
            CornerRadius = new CornerRadius(7)
        };
        AttachHoverFeedback(button);
        return button;
    }

    private static BitmapImage CreateBitmap(string path, int? decodePixelWidth = null) => new()
    {
        CreateOptions = BitmapCreateOptions.None,
        DecodePixelWidth = decodePixelWidth ?? 0,
        UriSource = new Uri(path)
    };

    private void PlayWorkflowEnterAnimation()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(_workflowHost, true);
        var visual = ElementCompositionPreview.GetElementVisual(_workflowHost);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 0;

        var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, CreateMainEase(visual));
        opacity.Duration = WorkflowEnterDuration;

        var translation = visual.Compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, new Vector3(0, 14, 0));
        translation.InsertKeyFrame(1f, Vector3.Zero, CreateMainEase(visual));
        translation.Duration = WorkflowEnterDuration;
        visual.StartAnimation("Opacity", opacity);
        visual.StartAnimation("Translation", translation);
    }

    private static void PlayDetailTriggerFeedback(UIElement trigger)
    {
        var visual = ElementCompositionPreview.GetElementVisual(trigger);
        if (trigger is FrameworkElement element)
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        visual.Scale = new Vector3(0.985f, 0.985f, 1);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, Vector3.One, CreateDetailEase(visual));
        animation.Duration = TimeSpan.FromMilliseconds(160);
        visual.StartAnimation("Scale", animation);
    }

    private static void AttachHoverFeedback(UIElement target)
    {
        target.PointerEntered += (_, _) => PlayHoverFeedback(target, 1.01f, TimeSpan.FromMilliseconds(150));
        target.PointerExited += (_, _) => PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(100));
    }

    private static void AttachCardInteractionFeedback(UIElement target)
    {
        var isPressed = false;
        var pressCanceled = false;
        var isReleasingCapture = false;

        target.PointerEntered += (_, _) =>
        {
            if (!isPressed)
                PlayHoverFeedback(target, 1.01f, TimeSpan.FromMilliseconds(150));
        };
        target.PointerExited += (_, _) =>
        {
            if (isPressed)
                pressCanceled = true;
            PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(100));
        };
        target.PointerPressed += (_, e) =>
        {
            isPressed = true;
            pressCanceled = false;
            target.CapturePointer(e.Pointer);
            PlayHoverFeedback(target, 0.975f, TimeSpan.FromMilliseconds(80));
        };
        target.PointerMoved += (_, e) =>
        {
            if (!isPressed || target is not FrameworkElement element)
                return;
            var position = e.GetCurrentPoint(target).Position;
            var isInside = position.X >= 0d
                && position.Y >= 0d
                && position.X <= element.ActualWidth
                && position.Y <= element.ActualHeight;
            if (pressCanceled == !isInside)
                return;
            pressCanceled = !isInside;
            PlayHoverFeedback(
                target,
                isInside ? 0.975f : 1f,
                TimeSpan.FromMilliseconds(isInside ? 80 : 110));
        };
        target.PointerReleased += (_, e) =>
        {
            if (!isPressed)
                return;
            isPressed = false;
            isReleasingCapture = true;
            target.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;
            if (pressCanceled)
                PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(110));
            else
                PlayDetailTriggerFeedback(target);
        };
        target.PointerCanceled += (_, e) =>
        {
            isPressed = false;
            pressCanceled = true;
            isReleasingCapture = true;
            target.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;
            PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(110));
        };
        target.PointerCaptureLost += (_, _) =>
        {
            if (isReleasingCapture)
                return;
            isPressed = false;
            pressCanceled = true;
            PlayHoverFeedback(target, 1f, TimeSpan.FromMilliseconds(110));
        };
    }

    private static void PlayHoverFeedback(UIElement target, float scale, TimeSpan duration)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        if (target is FrameworkElement element)
            visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1), CreateDetailEase(visual));
        animation.Duration = duration;
        visual.StartAnimation("Scale", animation);
    }

    private static Microsoft.UI.Composition.CubicBezierEasingFunction CreateMainEase(Microsoft.UI.Composition.Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

    private static Microsoft.UI.Composition.CubicBezierEasingFunction CreateDetailEase(Microsoft.UI.Composition.Visual visual) =>
        visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
}
