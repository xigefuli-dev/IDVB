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
    private void ShowMarkerEditor()
    {
        if (_draft is null || !HasAnyFloorImage(_draft))
            return;

        EnterModernEditorEnvironment();
        _draft.Recognition.EnsureStandardAnchors();
        var floors = _draft.Floors.OrderBy(floor => floor.SortOrder).ToArray();
        if (floors.Length == 0)
            return;
        if (!floors.Any(floor => string.Equals(floor.Key, _activeFloorKey, StringComparison.OrdinalIgnoreCase)))
            _activeFloorKey = floors[0].Key;

        _modernToolState.FirstFloorKey = floors[0].Key;
        _modernToolState.ActiveFloorKey = _activeFloorKey;
        _modernToolState.Reset();
        _modernContinuousLineStart = null;
        _modernCreationUndoStack.Clear();
        _modernSelection = null;
        _modernInteraction = EditorInteractionKind.None;
        _modernFocusMode = false;
        _modernLayerDrawerOpen = false;
        _hiddenEditorGroups.Clear();
        _hiddenEditorItems.Clear();
        _editorGroupExpansion.Clear();
        _editorToolButtons.Clear();

        var viewportWidth = Math.Max(1, ParentScrollViewer?.ActualWidth ?? ActualWidth);
        var viewportHeight = Math.Max(1, ParentScrollViewer?.ActualHeight ?? ActualHeight);
        _workflowHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        _workflowHost.VerticalAlignment = VerticalAlignment.Stretch;
        _workflowHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _workflowHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid
        {
            Background = new SolidColorBrush(EditorBackground),
            Width = viewportWidth,
            Height = viewportHeight,
            MinWidth = 0,
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        _modernEditorRoot = root;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _modernEditorHeader = CreateModernEditorHeader(floors);
        root.Children.Add(_modernEditorHeader);

        var body = new Grid { Margin = new Thickness(10, 0, 10, 10), ColumnSpacing = 9 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _modernLayerColumn = new ColumnDefinition { Width = new GridLength(286) };
        body.ColumnDefinitions.Add(_modernLayerColumn);
        body.Children.Add(CreateModernToolRail());

        var center = new Grid();
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.Children.Add(CreateModernViewport());
        var viewToolbar = CreateModernViewToolbar();
        Grid.SetRow(viewToolbar, 1);
        center.Children.Add(viewToolbar);
        Grid.SetColumn(center, 1);
        body.Children.Add(center);

        _modernLayerPane = CreateModernLayerPane();
        Grid.SetColumn(_modernLayerPane, 2);
        body.Children.Add(_modernLayerPane);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        root.SizeChanged += ModernEditorRoot_SizeChanged;
        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        SwitchModernFloor(_activeFloorKey, fitWhenLoaded: true);
        RefreshModernToolVisuals();
        RefreshModernLayerList();
        UpdateMarkerConfirmState();
        _ = LoadEditorPreferencesAsync();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ParentScrollViewer is not null)
                ApplyModernViewportSize(ParentScrollViewer.ActualWidth, ParentScrollViewer.ActualHeight);
        });
    }

    private Grid CreateModernEditorHeader(IReadOnlyList<FloorDefinition> floors)
    {
        var header = new Grid
        {
            Padding = new Thickness(22, 12, 16, 10),
            Background = new SolidColorBrush(Color.FromArgb(255, 14, 22, 32))
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

        var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        identity.Children.Add(new FontIcon
        {
            Glyph = "\uE809",
            FontSize = 30,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 48, 187, 255)),
            VerticalAlignment = VerticalAlignment.Center
        });
        var titles = new StackPanel { Spacing = 1 };
        titles.Children.Add(new TextBlock
        {
            Text = "地图编辑器",
            FontSize = 19,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(EditorText)
        });
        titles.Children.Add(new TextBlock
        {
            Text = "Identity Vision Bridge",
            FontSize = 12,
            Foreground = new SolidColorBrush(EditorMuted)
        });
        identity.Children.Add(titles);
        header.Children.Add(identity);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var floor in floors)
        {
            var floorKey = floor.Key;
            var selected = string.Equals(floorKey, _activeFloorKey, StringComparison.OrdinalIgnoreCase);
            var button = new Button
            {
                Content = floor.DisplayName,
                Tag = floorKey,
                MinWidth = 104,
                Height = 43,
                Padding = new Thickness(18, 7, 18, 7),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = new SolidColorBrush(selected ? AccentBlue : EditorPanel),
                Foreground = new SolidColorBrush(EditorText),
                BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(255, 73, 169, 255) : EditorBorder),
                BorderThickness = new Thickness(1)
            };
            button.Click += (_, _) => SwitchModernFloor(floorKey, fitWhenLoaded: true);
            tabs.Children.Add(button);
        }
        var tabScroller = new ScrollViewer
        {
            Content = tabs,
            HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(tabScroller, 1);
        header.Children.Add(tabScroller);
        return header;
    }

    private Border CreateModernToolRail()
    {
        var rail = new StackPanel { Spacing = 10 };
        var regular = new StackPanel { Spacing = 2 };
        regular.Children.Add(CreateModernToolButton(MapEditorTool.Select, "\uE8B0", "选择"));
        regular.Children.Add(CreateModernToolButton(MapEditorTool.Text, "\uE8D2", "文字"));
        regular.Children.Add(CreateModernToolButton(MapEditorTool.Line, "\uE8A1", "直线"));
        regular.Children.Add(CreateModernToolButton(MapEditorTool.Rectangle, "\uE7FB", "矩形"));
        rail.Children.Add(CreateToolGroup(regular));

        var special = new StackPanel { Spacing = 2 };
        special.Children.Add(CreateModernToolButton(MapEditorTool.Gate, "\uE839", "门特征"));
        special.Children.Add(CreateModernToolButton(MapEditorTool.Crop, "\uE7A8", "裁剪"));
        special.Children.Add(CreateModernToolButton(MapEditorTool.Anchor, "\uE707", "锚点"));
        special.Children.Add(CreateModernToolButton(MapEditorTool.Conceal, "\uE74A", "遮瑕"));
        special.Children.Add(CreateModernColorButton());
        rail.Children.Add(CreateToolGroup(special));

        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(7),
            Background = new SolidColorBrush(Color.FromArgb(255, 12, 20, 29)),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = rail
        };
    }

    private static Border CreateToolGroup(UIElement content) => new()
    {
        Background = new SolidColorBrush(EditorPanel),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(2),
        Child = content
    };

    private Button CreateModernToolButton(MapEditorTool tool, string glyph, string label)
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20 });
        content.Children.Add(new TextBlock { Text = label, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Tag = tool,
            Width = 56,
            Height = 60,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(EditorText)
        };
        button.Click += (_, _) => SelectModernTool(tool, button);
        _editorToolButtons[tool] = button;
        return button;
    }

    private Button CreateModernColorButton()
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        _modernColorIndicator = new Border
        {
            Width = 23,
            Height = 23,
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(EditorText),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(ParseEditorColor(_currentAnnotationColor))
        };
        content.Children.Add(_modernColorIndicator);
        content.Children.Add(new TextBlock { Text = "颜色", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button
        {
            Content = content,
            Width = 56,
            Height = 60,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Foreground = new SolidColorBrush(EditorText),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Click += (_, _) => ShowModernColorPicker(button);
        return button;
    }

    private Border CreateModernViewport()
    {
        _modernScene = new Grid
        {
            Width = 1280,
            Height = 720,
            Background = new SolidColorBrush(Color.FromArgb(255, 8, 12, 18)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _modernImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        _modernScene.Children.Add(_modernImage);
        _modernCanvas = new Canvas
        {
            Width = 1280,
            Height = 720,
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
        };
        _modernCanvas.PointerPressed += ModernCanvas_PointerPressed;
        _modernCanvas.PointerMoved += ModernCanvas_PointerMoved;
        _modernCanvas.PointerReleased += ModernCanvas_PointerReleased;
        _modernCanvas.PointerCanceled += ModernCanvas_PointerCanceled;
        _modernCanvas.PointerWheelChanged += ModernCanvas_PointerWheelChanged;
        _modernCanvas.DoubleTapped += ModernCanvas_DoubleTapped;
        _modernScene.Children.Add(_modernCanvas);

        _modernViewport = new ScrollViewer
        {
            Content = _modernScene,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = .1f,
            MaxZoomFactor = 8f,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(255, 5, 10, 16))
        };
        _modernViewport.ViewChanged += (_, _) =>
        {
            UpdateModernZoomText();
            RenderModernEditor();
        };
        _modernViewport.SizeChanged += (_, _) =>
        {
            if (_modernBitmap is { PixelWidth: > 0 })
                DispatcherQueue.TryEnqueue(() => FitModernCanvas());
        };
        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(255, 5, 10, 16)),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _modernViewport
        };
    }

}
