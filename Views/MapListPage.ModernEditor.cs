using IDVBuff.Features.Maps;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private enum EditorSelectionKind { Annotation, Anchor, Crop, Background }
    private enum EditorInteractionKind { None, Create, Move, Resize, Pan }

    private sealed record EditorSelection(EditorSelectionKind Kind, Guid? Id = null);

    private static readonly Color EditorBackground = Color.FromArgb(255, 8, 14, 22);
    private static readonly Color EditorPanel = Color.FromArgb(255, 18, 27, 39);
    private static readonly Color EditorPanelRaised = Color.FromArgb(255, 25, 36, 50);
    private static readonly Color EditorBorder = Color.FromArgb(255, 46, 62, 79);
    private static readonly Color EditorText = Color.FromArgb(255, 226, 234, 245);
    private static readonly Color EditorMuted = Color.FromArgb(255, 151, 166, 187);

    private readonly MapEditorToolState _modernToolState = new();
    private readonly RecentAnnotationColors _recentAnnotationColors = new();
    private MapEditorPreferences _editorPreferenceState = new();
    private readonly HashSet<string> _hiddenEditorGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _hiddenEditorItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _editorGroupExpansion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Expander> _modernLayerGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<MapEditorTool, Button> _editorToolButtons = [];
    private readonly MapEditorPreferencesRepository _editorPreferences = new(
        System.IO.Path.Combine(AppDataPaths.RootDirectory, "MapEditor", "preferences.json"));

    private bool _modernEditorActive;
    private bool _recentColorsLoaded;
    private FrameworkElement? _editorThemeRoot;
    private ElementTheme _editorPreviousTheme;
    private ScrollMode _editorPreviousVerticalScrollMode;
    private ScrollMode _editorPreviousHorizontalScrollMode;
    private ScrollBarVisibility _editorPreviousVerticalBarVisibility;
    private ScrollBarVisibility _editorPreviousHorizontalBarVisibility;
    private Grid? _modernEditorRoot;
    private Grid? _modernEditorHeader;
    private Border? _modernLayerPane;
    private ColumnDefinition? _modernLayerColumn;
    private Button? _modernLayerDrawerButton;
    private Button? _modernExportButton;
    private ScrollViewer? _modernViewport;
    private Grid? _modernScene;
    private Image? _modernImage;
    private Canvas? _modernCanvas;
    private StackPanel? _modernLayerList;
    private TextBlock? _modernStatusText;
    private TextBlock? _modernZoomText;
    private Border? _modernColorIndicator;
    private BitmapImage? _modernBitmap;
    private EditorSelection? _modernSelection;
    private EditorInteractionKind _modernInteraction;
    private Point _modernPointerStart;
    private Point _modernPointerCurrent;
    private Point _modernPanStart;
    private double _modernPanHorizontalOffset;
    private double _modernPanVerticalOffset;
    private string _modernResizeHandle = string.Empty;
    private NormalizedRectangle? _modernOriginalBounds;
    private NormalizedPoint? _modernOriginalStart;
    private NormalizedPoint? _modernOriginalEnd;
    private NormalizedRectangle? _modernPendingBounds;
    private NormalizedPoint? _modernPendingStart;
    private NormalizedPoint? _modernPendingEnd;
    private string _currentAnnotationColor = MapAnnotationColor.Default;
    private bool _modernGridVisible = true;
    private bool _modernSnapEnabled = true;
    private bool _modernFocusMode;
    private bool _modernLayersAreDrawer;
    private bool _modernLayerDrawerOpen;
    private bool _modernPointerMoved;
    private bool _modernExportRendering;
    private bool _modernExportInProgress;
    private uint? _modernCapturedPointerId;
    private NormalizedPoint? _modernContinuousLineStart;
    private readonly Stack<ModernUndoAction> _modernCreationUndoStack = new();
    private readonly MapConcealStrokeBuilder _modernConcealStroke = new();
    private NormalizedPoint? _modernConcealHoverPoint;

    private sealed record ModernUndoAction(
        string FloorKey,
        string Description,
        Action Undo,
        NormalizedPoint? ContinuousRestartPoint = null);

    private Border CreateModernViewToolbar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        row.Children.Add(CreateViewButton("\uE7C2", "平移", (_, _) => SelectModernTool(MapEditorTool.Pan)));
        row.Children.Add(CreateViewButton("\uE9A6", "适应画布", (_, _) => FitModernCanvas()));
        row.Children.Add(CreateToggleViewButton("网格", _modernGridVisible, (_, toggle) =>
        {
            _modernGridVisible = toggle.IsChecked is true;
            RenderModernEditor();
        }));
        row.Children.Add(CreateToggleViewButton("对齐", _modernSnapEnabled, (_, toggle) => _modernSnapEnabled = toggle.IsChecked is true));
        row.Children.Add(CreateViewButton("\uE738", "缩小", (_, _) => ChangeModernZoom(.8f)));
        _modernZoomText = new TextBlock
        {
            Text = "100%",
            Width = 58,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(EditorText)
        };
        row.Children.Add(_modernZoomText);
        row.Children.Add(CreateViewButton("\uE710", "放大", (_, _) => ChangeModernZoom(1.25f)));
        row.Children.Add(CreateViewButton("\uE740", "专注模式", (_, _) => ToggleModernFocusMode()));
        _modernExportButton = CreateViewButton("\uE74E", "导出 PNG", async (_, _) => await ShowModernPngExportDialogAsync());
        row.Children.Add(_modernExportButton);
        _modernLayerDrawerButton = CreateViewButton("\uE8A9", "图层", (_, _) => ToggleModernLayerDrawer());
        _modernLayerDrawerButton.Visibility = Visibility.Collapsed;
        row.Children.Add(_modernLayerDrawerButton);

        var toolbarScroller = new ScrollViewer
        {
            Content = row,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 13, 21, 31)),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = toolbarScroller
        };
    }

    private static Button CreateViewButton(string glyph, string toolTip, RoutedEventHandler click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 15 },
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Foreground = new SolidColorBrush(EditorText)
        };
        ToolTipService.SetToolTip(button, toolTip);
        button.Click += click;
        return button;
    }

    private static ToggleButton CreateToggleViewButton(string label, bool isChecked, Action<object, ToggleButton> click)
    {
        var button = new ToggleButton
        {
            Content = label,
            IsChecked = isChecked,
            Height = 34,
            Padding = new Thickness(9, 3, 9, 3),
            CornerRadius = new CornerRadius(6),
            Foreground = new SolidColorBrush(EditorText)
        };
        button.Click += (sender, _) => click(sender, button);
        return button;
    }

    private Border CreateModernLayerPane()
    {
        var pane = new Grid();
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var paneHeader = new StackPanel { Spacing = 3, Padding = new Thickness(14, 12, 14, 9) };
        paneHeader.Children.Add(new TextBlock
        {
            Text = "图层管理器",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(EditorText)
        });
        _modernStatusText = new TextBlock
        {
            Text = "选择一个工具开始编辑。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(EditorMuted)
        };
        paneHeader.Children.Add(_modernStatusText);
        pane.Children.Add(paneHeader);
        _modernLayerGroups.Clear();
        _modernLayerList = new StackPanel { Spacing = 3 };
        var listScroller = new ScrollViewer
        {
            Content = _modernLayerList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(listScroller, 1);
        pane.Children.Add(listScroller);

        var actions = new Grid { ColumnSpacing = 10, Padding = new Thickness(10) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var cancel = CreateEditorActionButton("取消", Color.FromArgb(255, 48, 61, 78));
        cancel.Click += async (_, _) =>
        {
            ResetBatchOperation();
            _draft = null;
            await ShowListAsync();
        };
        actions.Children.Add(cancel);
        _markerConfirmButton = CreateEditorActionButton("确认", EditorPanelRaised);
        _markerConfirmButton.Click += async (_, _) => await SaveDraftAsync();
        Grid.SetColumn(_markerConfirmButton, 1);
        actions.Children.Add(_markerConfirmButton);
        Grid.SetRow(actions, 2);
        pane.Children.Add(actions);

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(EditorPanel),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = pane
        };
    }

    private static Button CreateEditorActionButton(string text, Color background) => new()
    {
        Content = text,
        Height = 46,
        Background = new SolidColorBrush(background),
        Foreground = new SolidColorBrush(EditorText),
        BorderBrush = new SolidColorBrush(EditorBorder),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(7),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center
    };

    private void EnterModernEditorEnvironment()
    {
        if (_modernEditorActive)
            return;
        _modernEditorActive = true;
        NavigationCompactStateChanged?.Invoke(true);
        // Scope the temporary theme to this control. XamlRoot.Content can be a
        // different host (Frame/Grid) depending on how the page was navigated,
        // which made the old save/restore path a no-op in some editor flows.
        _editorThemeRoot = this;
        _editorPreviousTheme = RequestedTheme;
        RequestedTheme = ElementTheme.Dark;
        if (ParentScrollViewer is not null)
        {
            _editorPreviousVerticalScrollMode = ParentScrollViewer.VerticalScrollMode;
            _editorPreviousHorizontalScrollMode = ParentScrollViewer.HorizontalScrollMode;
            _editorPreviousVerticalBarVisibility = ParentScrollViewer.VerticalScrollBarVisibility;
            _editorPreviousHorizontalBarVisibility = ParentScrollViewer.HorizontalScrollBarVisibility;
            ParentScrollViewer.VerticalScrollMode = ScrollMode.Disabled;
            ParentScrollViewer.HorizontalScrollMode = ScrollMode.Disabled;
            ParentScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ParentScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ParentScrollViewer.ChangeView(0, 0, null, true);
            ParentScrollViewer.SizeChanged -= ModernParentViewport_SizeChanged;
            ParentScrollViewer.SizeChanged += ModernParentViewport_SizeChanged;
        }
    }

    private void ResetModernMarkerEditorSession()
    {
        if (!_modernEditorActive)
            return;
        CancelModernInteraction(restoreGeometry: true);
        if (_modernImage is not null)
            _modernImage.Source = null;
        if (_modernScene is not null)
            _modernScene.Children.Clear();
        if (_modernEditorRoot is not null)
            _modernEditorRoot.Children.Clear();
        _modernEditorActive = false;
        NavigationCompactStateChanged?.Invoke(false);
        if (_editorThemeRoot is not null)
            _editorThemeRoot.RequestedTheme = _editorPreviousTheme;
        _editorThemeRoot = null;
        if (ParentScrollViewer is not null)
        {
            ParentScrollViewer.SizeChanged -= ModernParentViewport_SizeChanged;
            ParentScrollViewer.VerticalScrollMode = _editorPreviousVerticalScrollMode;
            ParentScrollViewer.HorizontalScrollMode = _editorPreviousHorizontalScrollMode;
            ParentScrollViewer.VerticalScrollBarVisibility = _editorPreviousVerticalBarVisibility;
            ParentScrollViewer.HorizontalScrollBarVisibility = _editorPreviousHorizontalBarVisibility;
        }
        _modernEditorRoot = null;
        _modernEditorHeader = null;
        _modernLayerPane = null;
        _modernLayerColumn = null;
        _modernViewport = null;
        _modernScene = null;
        _modernImage = null;
        _modernCanvas = null;
        _modernLayerList = null;
        _modernExportButton = null;
        _modernBitmap = null;
        _modernSelection = null;
        _modernExportRendering = false;
        _modernExportInProgress = false;
        _modernToolState.Reset();
    }

    private async Task LoadEditorPreferencesAsync()
    {
        if (_recentColorsLoaded)
            return;
        _editorPreferenceState = await _editorPreferences.LoadAsync();
        _recentAnnotationColors.Replace(_editorPreferenceState.RecentColors.AsEnumerable().Reverse());
        _recentColorsLoaded = true;
    }

    private async Task RememberEditorColorAsync(string color)
    {
        if (!_recentAnnotationColors.Use(color))
            return;
        try
        {
            _editorPreferenceState.RecentColors = _recentAnnotationColors.Colors.ToList();
            await _editorPreferences.SaveAsync(_editorPreferenceState);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to save map editor colors: {exception.Message}");
        }
    }

    private void ShowModernColorPicker(Button placementTarget)
    {
        var panel = new StackPanel { Spacing = 8, Width = 300 };
        panel.Children.Add(new TextBlock
        {
            Text = "最近使用",
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });
        var recents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, MinHeight = 34 };
        foreach (var recent in _recentAnnotationColors.Colors)
        {
            var captured = recent;
            var swatch = new Button
            {
                Width = 31,
                Height = 31,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(ParseEditorColor(recent)),
                BorderBrush = new SolidColorBrush(EditorText),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16)
            };
            swatch.Click += (_, _) => ApplySelectedEditorColor(captured);
            recents.Children.Add(swatch);
        }
        panel.Children.Add(recents);
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            Color = ParseEditorColor(_currentAnnotationColor)
        };
        picker.ColorChanged += (_, args) => ApplySelectedEditorColor(ToEditorColorHex(args.NewColor));
        panel.Children.Add(picker);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.RightEdgeAlignedTop };
        flyout.ShowAt(placementTarget);
    }

    private void ApplySelectedEditorColor(string color)
    {
        if (!MapAnnotationColor.TryNormalize(color, out var normalized))
            return;
        _currentAnnotationColor = normalized;
        if (_modernColorIndicator is not null)
            _modernColorIndicator.Background = new SolidColorBrush(ParseEditorColor(normalized));
    }

    private async Task SaveEditorPreferencesAsync()
    {
        try
        {
            _editorPreferenceState.RecentColors = _recentAnnotationColors.Colors.ToList();
            await _editorPreferences.SaveAsync(_editorPreferenceState);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to save map editor preferences: {exception.Message}");
        }
    }

    private void SelectModernTool(MapEditorTool tool, Button? placementTarget = null)
    {
        if (_modernToolState.ActiveTool == tool
            && tool is MapEditorTool.Text or MapEditorTool.Line or MapEditorTool.Conceal)
        {
            if (placementTarget is not null)
            {
                if (tool == MapEditorTool.Text)
                    ShowModernTextProperties(placementTarget);
                else if (tool == MapEditorTool.Line)
                    ShowModernLineProperties(placementTarget);
                else
                    ShowModernConcealProperties(placementTarget);
            }
            return;
        }
        EndModernContinuousLine();
        _modernToolState.ActiveFloorKey = _activeFloorKey;
        _modernToolState.Select(tool);
        CancelModernInteraction(restoreGeometry: true);
        _modernSelection = null;
        SetModernStatus(tool == MapEditorTool.Gate
            ? _modernToolState.UsesPrimaryGatePair
                ? "请先拖动标记正门。"
                : "请拖动标记次要门特征。"
            : ModernToolHint(tool));
        RefreshModernToolVisuals();
        RenderModernEditor();
        RefreshModernLayerList();
    }

    private void EndModernContinuousLine() => _modernContinuousLineStart = null;

}
