using IDVBuff.Features.Maps;
using IDVBuff.Survey.Domain;
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
    private static readonly (Color LightFill, Color LightOutline, Color DarkFill, Color DarkOutline)[]
        VariantPalette =
    [
        (Hex("FDE7EC"), Hex("B4234D"), Hex("3A1722"), Hex("FF809F")),
        (Hex("FBE9E4"), Hex("B13A21"), Hex("3A1C16"), Hex("FF9275")),
        (Hex("FBEEDC"), Hex("A85B00"), Hex("382414"), Hex("FFB45B")),
        (Hex("F8F0CF"), Hex("8A6800"), Hex("32290E"), Hex("E8C84E")),
        (Hex("EEF0D4"), Hex("6C7300"), Hex("282B12"), Hex("C6D35C")),
        (Hex("E1F2E7"), Hex("1F7A3F"), Hex("143021"), Hex("65D58B")),
        (Hex("DDF2ED"), Hex("147363"), Hex("12302B"), Hex("5ED0B9")),
        (Hex("EEE7FA"), Hex("6842A6"), Hex("261B3A"), Hex("B69AE9")),
        (Hex("F2E5FA"), Hex("8038A5"), Hex("2D1738"), Hex("D899EF")),
        (Hex("FAE4F1"), Hex("9B2D70"), Hex("35162B"), Hex("E58AC0")),
        (Hex("FAE5E6"), Hex("9E3941"), Hex("34191C"), Hex("E68D94")),
        (Hex("F2E9E1"), Hex("7C5234"), Hex("2E2119"), Hex("D1A27E"))
    ];
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
    internal Action<bool>? NavigationCompactStateChanged { get; set; }
    private readonly Dictionary<Guid, Border> _cardBorders = [];
    private readonly Dictionary<string, BitmapImage> _previewImages =
        new(StringComparer.OrdinalIgnoreCase);
    private Button? _editButton;
    private Button? _deleteButton;
    private Button? _variantButton;
    private Button? _classEditButton;
    private Button? _importButton;
    private Button? _exportButton;
    private HashSet<Guid> _selectedMapIds = [];
    private Guid? _lastClickedMapId;
    private IReadOnlyList<MapRecord> _loadedMaps = [];
    private IReadOnlyList<MapVariantGroup> _variantGroups = [];
    private IReadOnlyList<SurveyProjectSummary> _surveyProjects = [];
    private IReadOnlyList<string> _classes = ["S1"];
    private IReadOnlyDictionary<string, MapClassProperties> _classProperties =
        new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
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
        ActualThemeChanged += (_, _) => UpdateSelectedCardVisuals();
        KeyDown += MapListPage_KeyDown;
    }

    private bool HasSelection => _selectedMapIds.Count > 0;

    private static Color Hex(string value) => Color.FromArgb(
        255,
        Convert.ToByte(value[..2], 16),
        Convert.ToByte(value.Substring(2, 2), 16),
        Convert.ToByte(value.Substring(4, 2), 16));

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
        if (HandleModernMarkerEditorKeyDown(e))
            return;

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
        _classProperties = snapshot.ClassProperties;
        _loadedMaps = snapshot.Maps;
        _variantGroups = snapshot.VariantGroups;
        _surveyProjects = await App.Session.GetSurveyProjectsAsync();
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
        _importButton = importButton;
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
        _variantButton = CreateActionButton("🔗", AccentBlue);
        // This action is icon-only; do not let the text-button defaults make
        // it consume the same width as the labelled actions beside it.
        _variantButton.Width = 45;
        _variantButton.MinWidth = 45;
        _variantButton.Padding = new Thickness(0);
        _variantButton.IsEnabled = _selectedMapIds.Count >= 2;
        ToolTipService.SetToolTip(_variantButton, "绑定/解绑变体");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            _variantButton,
            "绑定或解绑地图变体");
        _variantButton.Click += async (_, _) => await ToggleSelectedVariantGroupAsync();
        actions.Children.Add(_variantButton);
        actions.Children.Add(CreateClassPicker());
        actionRow.Children.Add(actions);

        var exportButton = CreateActionButton("导出", AccentBlue);
        _exportButton = exportButton;
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
        scrollContent.Children.Add(CreateSurveyProjectsSection());

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
            Background = FluentTheme.Brush("LayerFillColorDefaultBrush"),
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

        // Top layer: transparent frozen controls, inset 5% from both sides.
        var buttonBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 8, 0, 12),
            Child = actionRow
        };
        var buttonBarLayout = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };
        buttonBarLayout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(5, GridUnitType.Star)
        });
        buttonBarLayout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(90, GridUnitType.Star)
        });
        buttonBarLayout.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(5, GridUnitType.Star)
        });
        Grid.SetColumn(buttonBar, 1);
        buttonBarLayout.Children.Add(buttonBar);
        root.Children.Add(buttonBarLayout);

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
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
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

        var reorder = CreateSecondaryButton("重新排序");
        reorder.MinWidth = 0;
        reorder.MinHeight = 45;
        reorder.Padding = new Thickness(12, 0, 12, 0);
        reorder.Click += async (_, _) => await ReorderCurrentClassAsync();
        controls.Children.Add(reorder);

        _classEditButton = CreateSecondaryButton("地图类编辑");
        _classEditButton.MinWidth = 0;
        _classEditButton.MinHeight = 45;
        _classEditButton.Padding = new Thickness(12, 0, 12, 0);
        _classEditButton.IsEnabled = !_isPackageOperation;
        _classEditButton.Click += async (_, _) => await ShowClassPropertiesDialogAsync();
        controls.Children.Add(_classEditButton);

        return controls;
    }

    private Button CreateRenameClassButton()
    {
        var button = new Button
        {
            Background = FluentTheme.Brush("ControlFillColorDefaultBrush"),
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(4)
        };
        var icon = new SymbolIcon(Symbol.Edit);
        icon.Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush");
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
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
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
        return new ComboBoxItem
        {
            Content = row,
            Tag = className,
            MinHeight = 38,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };
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

}
