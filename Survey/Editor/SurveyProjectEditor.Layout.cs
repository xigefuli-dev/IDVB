using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor
{
    private static readonly Color EditorBackground = Color.FromArgb(255, 8, 14, 22);
    private static readonly Color EditorPanel = Color.FromArgb(255, 18, 27, 39);
    private static readonly Color EditorBorder = Color.FromArgb(255, 46, 62, 79);
    private static readonly Color EditorText = Color.FromArgb(255, 226, 234, 245);
    private static readonly Color AccentBlue = Color.FromArgb(255, 46, 132, 225);

    private FrameworkElement BuildEditorLayout()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid { Background = new SolidColorBrush(EditorBackground) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(CreateHeader());

        var workspace = new Grid
        {
            Margin = new Thickness(10, 0, 10, 10),
            ColumnSpacing = 9,
            MinHeight = 0
        };
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(286) });
        workspace.Children.Add(CreateToolRail());

        var center = new Grid { MinWidth = 0, MinHeight = 0 };
        center.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        center.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        center.Children.Add(new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _canvas
        });
        var sizeBar = CreateSizeBar();
        Grid.SetRow(sizeBar, 1);
        center.Children.Add(sizeBar);
        Grid.SetColumn(center, 1);
        workspace.Children.Add(center);

        Grid.SetColumn(_layers, 2);
        workspace.Children.Add(_layers);
        Grid.SetRow(workspace, 1);
        root.Children.Add(workspace);
        SelectTool(SurveyEditorTool.Select);
        return root;
    }

    private Grid CreateHeader()
    {
        var header = new Grid
        {
            Padding = new Thickness(16, 9, 16, 9),
            ColumnSpacing = 12,
            Background = new SolidColorBrush(Color.FromArgb(255, 14, 22, 32))
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = CreateHeaderButton("返回");
        back.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        header.Children.Add(back);

        var identity = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        _title.FontSize = 18;
        _title.Foreground = new SolidColorBrush(EditorText);
        identity.Children.Add(_title);
        _status.FontSize = 11;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        identity.Children.Add(_status);
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(new TextBlock
        {
            Text = "楼层",
            Foreground = new SolidColorBrush(EditorText),
            VerticalAlignment = VerticalAlignment.Center
        });
        _floorPicker.Foreground = new SolidColorBrush(EditorText);
        _floorPicker.SelectionChanged += FloorPicker_SelectionChanged;
        actions.Children.Add(_floorPicker);
        ConfigureHeaderButton(_undoButton);
        _undoButton.Click += async (_, _) => await _session.UndoAsync();
        actions.Children.Add(_undoButton);
        ConfigureHeaderButton(_redoButton);
        _redoButton.Click += async (_, _) => await _session.RedoAsync();
        actions.Children.Add(_redoButton);
        var properties = CreateHeaderButton("项目属性");
        properties.Click += async (_, _) => await ShowProjectPropertiesAsync();
        actions.Children.Add(properties);
        var exportPng = CreateHeaderButton("导出为 PNG");
        exportPng.Click += async (_, _) => await ExportCurrentFloorPngAsync();
        actions.Children.Add(exportPng);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        return header;
    }

    private Border CreateToolRail()
    {
        var tools = new StackPanel { Spacing = 2 };
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Select, "\uE8B0", "选择"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Pan, "\uE7C2", "拖动"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Decontaminate, "\uE790", "去污"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Align, "\uE73E", "魔术贴"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.NormalizeColors, "\uE790", "融色"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Eraser, "\uE75C", "橡皮擦"));
        return new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(7),
            Background = new SolidColorBrush(Color.FromArgb(255, 12, 20, 29)),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = tools
        };
    }

    private Button CreateToolButton(SurveyEditorTool tool, string glyph, string label)
    {
        var content = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 20 });
        content.Children.Add(new TextBlock { Text = label, FontSize = 10 });
        var button = new Button
        {
            Content = content,
            Width = 56,
            Height = 60,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Foreground = new SolidColorBrush(EditorText)
        };
        button.Click += (_, _) =>
        {
            if (tool == SurveyEditorTool.Eraser
                && _canvas.ActiveTool == SurveyEditorTool.Eraser)
            {
                ShowEraserProperties(button);
                return;
            }
            SelectTool(tool);
        };
        _toolButtons[tool] = button;
        return button;
    }

    private Border CreateSizeBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(10, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        row.Children.Add(new TextBlock
        {
            Text = "画面大小",
            Foreground = new SolidColorBrush(EditorText),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(CreateViewButton("\uE9A6", "适应画布", (_, _) => _canvas.FitToViewport()));
        row.Children.Add(CreateViewButton("\uE738", "缩小", (_, _) => _canvas.ChangeZoom(0.8d)));
        _zoomPercent = new NumberBox
        {
            Value = 100d,
            Minimum = 10d,
            Maximum = 800d,
            SmallChange = 10d,
            Width = 82,
            Height = 34,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden
        };
        ToolTipService.SetToolTip(_zoomPercent, "输入整个画面的显示缩放百分比（10–800）");
        _zoomPercent.ValueChanged += (_, args) =>
        {
            if (!_updatingZoom && double.IsFinite(args.NewValue))
                _canvas.SetZoomPercent(args.NewValue);
        };
        row.Children.Add(_zoomPercent);
        row.Children.Add(new TextBlock
        {
            Text = "%",
            Foreground = new SolidColorBrush(EditorText),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(CreateViewButton("\uE710", "放大", (_, _) => _canvas.ChangeZoom(1.25d)));
        row.Children.Add(CreateViewButton("\uE777", "恢复 100%", (_, _) => _canvas.SetZoomPercent(100d)));
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(238, 13, 21, 31)),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = row
        };
    }

    private void SelectTool(SurveyEditorTool tool)
    {
        _canvas.SetTool(tool);
        foreach (var pair in _toolButtons)
            pair.Value.Background = new SolidColorBrush(
                pair.Key == tool ? AccentBlue : EditorPanel);
        SetStatus(tool switch
        {
            SurveyEditorTool.Select => "选择工具：单击选择图层，拖动可调整图层位置。",
            SurveyEditorTool.Pan => "拖动工具：可向任意方向拖动画布视图，不会修改图层位置。",
            SurveyEditorTool.Decontaminate => "去污工具：点击一个已选且未锁定图层，在原图与去污图之间切换。",
            SurveyEditorTool.Align => "魔术贴工具：多选图层后，在画布点击其中一层作为固定基准。",
            SurveyEditorTool.NormalizeColors => "融色工具：多选图层后，点击其中一层作为颜色基准。",
            SurveyEditorTool.Eraser => _eraseMode == SurveyEraseMode.Eraser
                ? "橡皮擦：在主选图层拖动以隐藏区域；再次点击工具可打开属性。"
                : "砂纸：在当前楼层全部可见未锁定图层上隐藏区域；再次点击工具可打开属性。",
            _ => string.Empty
        });
    }

    private void ShowEraserProperties(Button anchor)
    {
        if (_eraserFlyout is null)
            _eraserFlyout = CreateEraserPropertiesFlyout();
        _eraserFlyout.ShowAt(anchor);
    }

    private Flyout CreateEraserPropertiesFlyout()
    {
        var content = new StackPanel { Spacing = 10, Width = 248 };
        content.Children.Add(new TextBlock
        {
            Text = "橡皮擦属性",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        var mode = new ComboBox
        {
            Header = "模式",
            ItemsSource = new[] { "橡皮擦（主选图层）", "砂纸（全部可见未锁定图层）" },
            SelectedIndex = _eraseMode == SurveyEraseMode.Eraser ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(mode);
        var shape = new ComboBox
        {
            Header = "形状",
            ItemsSource = new[] { "圆形", "正方形" },
            SelectedIndex = _brushShape == SurveyBrushShape.Circle ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(shape);
        var size = new NumberBox
        {
            Header = "大小（地图画布像素）",
            Minimum = 1d,
            Maximum = 1024d,
            Value = _brushSize,
            SmallChange = 1d,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var slider = new Slider
        {
            Minimum = 1d,
            Maximum = 1024d,
            Value = _brushSize,
            StepFrequency = 1d
        };
        content.Children.Add(size);
        content.Children.Add(slider);
        var previewHost = new Grid
        {
            Width = 112,
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(255, 8, 14, 22))
        };
        content.Children.Add(previewHost);

        void UpdatePreview()
        {
            previewHost.Children.Clear();
            var visualSize = Math.Clamp(12d + (Math.Sqrt(_brushSize) * 2.7d), 14d, 100d);
            Microsoft.UI.Xaml.Shapes.Shape visual = _brushShape == SurveyBrushShape.Circle
                ? new Microsoft.UI.Xaml.Shapes.Ellipse()
                : new Microsoft.UI.Xaml.Shapes.Rectangle();
            visual.Width = visualSize;
            visual.Height = visualSize;
            visual.HorizontalAlignment = HorizontalAlignment.Center;
            visual.VerticalAlignment = VerticalAlignment.Center;
            visual.Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            visual.Fill = new SolidColorBrush(Color.FromArgb(70, 255, 110, 80));
            visual.StrokeThickness = 1.5d;
            previewHost.Children.Add(visual);
            _canvas.SetBrush(_brushSize, _brushShape);
            if (_canvas.ActiveTool == SurveyEditorTool.Eraser)
                SelectTool(SurveyEditorTool.Eraser);
        }

        mode.SelectionChanged += (_, _) =>
        {
            _eraseMode = mode.SelectedIndex == 1 ? SurveyEraseMode.Sandpaper : SurveyEraseMode.Eraser;
            UpdatePreview();
        };
        shape.SelectionChanged += (_, _) =>
        {
            _brushShape = shape.SelectedIndex == 1 ? SurveyBrushShape.Square : SurveyBrushShape.Circle;
            UpdatePreview();
        };
        var syncing = false;
        size.ValueChanged += (_, args) =>
        {
            if (syncing || !double.IsFinite(args.NewValue))
                return;
            syncing = true;
            _brushSize = Math.Clamp(args.NewValue, 1d, 1024d);
            slider.Value = _brushSize;
            syncing = false;
            UpdatePreview();
        };
        slider.ValueChanged += (_, args) =>
        {
            if (syncing)
                return;
            syncing = true;
            _brushSize = Math.Clamp(args.NewValue, 1d, 1024d);
            size.Value = Math.Round(_brushSize);
            syncing = false;
            UpdatePreview();
        };
        UpdatePreview();
        return new Flyout { Content = content };
    }

    private static Button CreateViewButton(string glyph, string tooltip, RoutedEventHandler click)
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
        ToolTipService.SetToolTip(button, tooltip);
        button.Click += click;
        return button;
    }

    private static Button CreateHeaderButton(string text) => new()
    {
        Content = text,
        MinHeight = 34,
        Padding = new Thickness(12, 5, 12, 5),
        Foreground = new SolidColorBrush(EditorText),
        Background = new SolidColorBrush(EditorPanel)
    };

    private static void ConfigureHeaderButton(Button button)
    {
        button.MinHeight = 34;
        button.Padding = new Thickness(12, 5, 12, 5);
        button.Foreground = new SolidColorBrush(EditorText);
        button.Background = new SolidColorBrush(EditorPanel);
    }
}
