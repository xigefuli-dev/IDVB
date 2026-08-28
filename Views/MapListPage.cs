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
    /// <summary>Returns the primary import action for onboarding emphasis.</summary>
    public FrameworkElement? GetImportControl() => _importButton;
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
        (Hex("EFCBD4"), Hex("B4234D"), Hex("3A1722"), Hex("FF809F")),
        (Hex("EFD0C7"), Hex("B13A21"), Hex("3A1C16"), Hex("FF9275")),
        (Hex("EED8BB"), Hex("A85B00"), Hex("382414"), Hex("FFB45B")),
        (Hex("E9DCAD"), Hex("8A6800"), Hex("32290E"), Hex("E8C84E")),
        (Hex("DDE2B9"), Hex("6C7300"), Hex("282B12"), Hex("C6D35C")),
        (Hex("CBE4D4"), Hex("1F7A3F"), Hex("143021"), Hex("65D58B")),
        (Hex("C7E4DC"), Hex("147363"), Hex("12302B"), Hex("5ED0B9")),
        (Hex("DDD2EF"), Hex("6842A6"), Hex("261B3A"), Hex("B69AE9")),
        (Hex("E4CDEE"), Hex("8038A5"), Hex("2D1738"), Hex("D899EF")),
        (Hex("EECBDD"), Hex("9B2D70"), Hex("35162B"), Hex("E58AC0")),
        (Hex("EDCACD"), Hex("9E3941"), Hex("34191C"), Hex("E68D94")),
        (Hex("E1D3C7"), Hex("7C5234"), Hex("2E2119"), Hex("D1A27E"))
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
    private IReadOnlyList<MapTagGroup> _filterGroups = [];
    private readonly Dictionary<Guid, HashSet<string>> _selectedTagFilters = [];
    private Border? _mapCardsSurface;
    private IReadOnlyList<MapVariantGroup> _variantGroups = [];
    private IReadOnlyList<SurveyProjectSummary> _surveyProjects = [];
    private IReadOnlyList<string> _classes = ["S1"];
    private IReadOnlyDictionary<string, MapClassProperties> _classProperties =
        new Dictionary<string, MapClassProperties>(StringComparer.OrdinalIgnoreCase);
    private string _selectedClass = "S1";
    private bool _hasInitializedClassSelection;
    private bool _surveyProjectsCollapsed = ShellLayoutMemory.Load().SurveyProjectsCollapsed;
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

            if (!App.IsSafeMode)
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
        if (!App.IsSafeMode)
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
        _filterGroups = (await new MapTagStore().LoadAsync(snapshot.Maps, snapshot.Classes))
            .Where(group => group.IsEnabled)
            .ToArray();
        _surveyProjects = App.IsSafeMode
            ? []
            : await App.Session.GetSurveyProjectsAsync();
        _previewImages.Clear();
        if (!_hasInitializedClassSelection)
        {
            // The match control panel and this page intentionally share the
            // same persisted preference. This page only reads it: changing
            // the local filter below must never update settings.json.
            _selectedClass = MapRuntimeSettingsRules.ResolveMapClass(
                _classes,
                App.IsSafeMode ? null : App.Session.LastSelectedMapClass)
                ?? _selectedClass;
            _hasInitializedClassSelection = true;
        }
        else if (!_classes.Any(name => string.Equals(
            name,
            _selectedClass,
            StringComparison.OrdinalIgnoreCase)))
        {
            _selectedClass = _classes[0];
        }
        ShowListFromLoadedSnapshot();
    }

}
