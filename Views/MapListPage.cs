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
public sealed partial class MapListPage : UserControl
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
    // TODO: 适配深色主题 — 当前硬编码为浅色主题色调
    private static readonly Color PageSurfaceColor = Color.FromArgb(255, 238, 238, 238);
    private static readonly Color ButtonDefaultBackground = Color.FromArgb(255, 242, 242, 242);
    private static readonly Color ButtonDarkForeground = Color.FromArgb(255, 43, 43, 43);
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
    internal ScrollViewer? ParentScrollViewer { get; set; }
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
        Unloaded += OnUnloaded;
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

            await App.Session.RefreshMapCacheAsync();
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

        // ── Button bar (actionRow) — built first, overlaid on top ──
        var actionRow = new Grid
        {
            Margin = new Thickness(0, 8, 0, 15)
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
            teachingTip.IsOpen = !teachingTip.IsOpen;
        };

        // ── Scrollable content (cards only) ──
        var scrollContent = new StackPanel { Spacing = 0 };

        // Spacer BELOW the frozen operation bar (increased + root margin) to prevent clipping of maps/ScrollBar
        scrollContent.Children.Add(new Border { Height = 80 });

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
            mapContent = cardsGrid;
        }

        var mapSurface = new Border
        {
            Background = new SolidColorBrush(PageSurfaceColor),
            CornerRadius = new CornerRadius(14),
            MinHeight = 459,
            Child = mapContent
        };
        scrollContent.Children.Add(mapSurface);

        // ── Root: overlay layout (Grid children stack in z-order) ──
        var root = new Grid { Margin = new Thickness(36, 24, 36, 38) };
        ApplyViewportConstraint(root);

        // Bottom layer: full-height scroll area (cards only) — now safe from frozen bar
        var pageScroller = new ScrollViewer
        {
            Content = scrollContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(pageScroller);

        // Top layer: frozen button bar (now matches page background, reduced height)
        var buttonBar = new Border
        {
            Background = new SolidColorBrush(PageSurfaceColor),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 8, 0, 12),
            Child = actionRow
        };
        root.Children.Add(buttonBar);

        root.Children.Add(teachingTip);

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

        var rename = CreateRenameClassButton();
        rename.Width = 48;
        rename.Height = 45;
        rename.Click += async (_, _) => await ShowRenameClassDialogAsync();

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(8, 0, 0, 0)
        };
        controls.Children.Add(picker);
        controls.Children.Add(rename);
        controls.Children.Add(add);
        controls.Children.Add(remove);

        var batchRename = CreateSecondaryButton("批量重命名");
        batchRename.MinWidth = 0;
        batchRename.MinHeight = 45;
        batchRename.Padding = new Thickness(12, 0, 12, 0);
        batchRename.Click += async (_, _) => await BatchRenameAllMapsToDefaultNamesAsync();
        controls.Children.Add(batchRename);

        return controls;
    }

    private Button CreateRenameClassButton()
    {
        var button = new Button
        {
            Background = new SolidColorBrush(ButtonDefaultBackground),
            Foreground = new SolidColorBrush(ButtonDarkForeground),
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4)
        };
        var icon = new SymbolIcon(Symbol.Edit);
        icon.Foreground = new SolidColorBrush(ButtonDarkForeground);
        button.Content = icon;
        AttachHoverFeedback(button);
        return button;
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
            await App.Session.RefreshMapCacheAsync();
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
            await App.Session.RefreshMapCacheAsync(map.Id);
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
}
