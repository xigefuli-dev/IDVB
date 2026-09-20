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
        var importImage = CreateHeaderButton("导入图片");
        importImage.Click += async (_, _) => await ImportImageAsync();
        actions.Children.Add(importImage);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        return header;
    }

    private Border CreateToolRail()
    {
        var tools = new StackPanel { Spacing = 2 };
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Select, "\uE8B0", "变换工具"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Pan, "\uE7C2", "拖动"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Decontaminate, "\uE790", "去污"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.VignetteCorrection, "\uE706", "反晕影"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Align, "\uE73E", "魔术贴"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.NormalizeColors, "\uE790", "融色"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Template, "\uE71C", "模板"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Eraser, "\uE75C", "橡皮擦"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.PaintBucket, "\uE71E", "颜料桶"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Brush, "\uE76B", "画笔"));
        tools.Children.Add(CreateToolButton(SurveyEditorTool.Eyedropper, "\uE71C", "吸管"));
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
            if (tool == SurveyEditorTool.VignetteCorrection)
            {
                SelectTool(tool);
                ShowVignetteProperties(button);
                return;
            }
            if (tool == SurveyEditorTool.Eraser
                && _canvas.ActiveTool == SurveyEditorTool.Eraser)
            {
                ShowEraserProperties(button);
                return;
            }
            if (tool is SurveyEditorTool.PaintBucket or SurveyEditorTool.Brush
                && _canvas.ActiveTool == tool)
            {
                ShowPaintProperties(button);
                return;
            }
            if (tool == SurveyEditorTool.Template)
            {
                SelectTool(tool);
                ShowTemplateFlyout(button);
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
            SurveyEditorTool.Select => "变换工具：框内拖动移动；边中点单轴缩放；角点等比缩放（Shift 自由缩放）；外角拖动旋转。",
            SurveyEditorTool.Pan => "拖动工具：可向任意方向拖动画布视图，不会修改图层位置。",
            SurveyEditorTool.Decontaminate => "去污工具：点击一个已选且未锁定图层，在原图与去污图之间切换。",
            SurveyEditorTool.VignetteCorrection => "晕影校正/反晕影：设置补偿起点与强度后应用到选中图层。",
            SurveyEditorTool.Align => "魔术贴工具：多选图层后，在画布点击其中一层作为固定基准。",
            SurveyEditorTool.NormalizeColors => "融色工具：多选图层后，点击其中一层作为颜色基准。",
            SurveyEditorTool.Template => _templateMode == SurveyTemplateMode.Create
                ? "模板工具：先选择填充、线框或图标，再用吸管从当前画面取色并保存模板。"
                : "模板工具：选择模板后点击一个图层即可套用；操作支持撤回和重做。",
            SurveyEditorTool.Eraser => _eraseMode == SurveyEraseMode.Eraser
                ? "橡皮擦：在主选图层拖动以隐藏区域；再次点击工具可打开属性。"
                : "砂纸：在当前楼层全部可见未锁定图层上隐藏区域；再次点击工具可打开属性。",
            SurveyEditorTool.Eyedropper => "吸管：按当前画面合成结果取色；吸管会保持选中，可连续取色。",
            _ => string.Empty
        });
    }

    private void ShowEraserProperties(Button anchor)
    {
        if (_eraserFlyout is null)
            _eraserFlyout = CreateEraserPropertiesFlyout();
        _eraserFlyout.ShowAt(anchor);
    }


    private void ShowVignetteProperties(Button anchor)
    {
        _vignetteFlyout ??= CreateVignettePropertiesFlyout();
        _vignetteFlyout.ShowAt(anchor);
    }

    private void ShowTemplateFlyout(Button anchor)
    {
        _templateFlyout ??= CreateTemplateFlyout();
        _templateFlyout.ShowAt(anchor);
    }

    private Flyout CreateTemplateFlyout()
    {
        var root = new StackPanel { Spacing = 10, Width = 310 };
        root.Children.Add(new TextBlock
        {
            Text = "模板工具",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        root.Children.Add(new TextBlock
        {
            Text = "新建模板记录多个语义颜色；套用模板按 Lab 色差和局部结构重新计算图层。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        _templateModePicker = new ComboBox
        {
            Header = "模式",
            ItemsSource = new[] { "新建模板", "套用模板" },
            SelectedIndex = _templateMode == SurveyTemplateMode.Create ? 0 : 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(_templateModePicker);
        var host = new StackPanel { Spacing = 8 };
        root.Children.Add(host);
        _templateModePicker.SelectionChanged += (_, _) =>
        {
            var requested = _templateModePicker.SelectedIndex == 1
                ? SurveyTemplateMode.Apply
                : SurveyTemplateMode.Create;
            if (requested == SurveyTemplateMode.Apply && _templates.Count == 0)
            {
                _templateModePicker.SelectedIndex = 0;
                SetStatus("请先保存至少一个模板，才能使用套用模板。", isError: true);
                return;
            }
            if (requested == SurveyTemplateMode.Apply && _editingTemplateId is not null)
            {
                _editingTemplateId = null;
                _draftTemplateEntries.Clear();
            }
            _templateMode = requested;
            _canvas.DisarmTemplateColorSampler();
            RebuildTemplatePanel(host);
            SetStatus(ModernTemplateModeHint());
        };
        RebuildTemplatePanel(host);
        return new Flyout { Content = root };
    }

    private void RebuildTemplatePanel(StackPanel host)
    {
        host.Children.Clear();
        _templateDraftList = null;
        _templatePicker = null;
        _templateNameBox = null;
        _templateSamplePreview = null;
        _templateSampleText = null;
        _templateSaveButton = null;
        _templateCancelEditButton = null;
        _templateSamplerButton = null;
        if (_templateMode == SurveyTemplateMode.Create)
            BuildCreateTemplatePanel(host);
        else
            BuildApplyTemplatePanel(host);
    }

    private void BuildCreateTemplatePanel(StackPanel host)
    {
        var editingTemplate = _editingTemplateId is { } editingId
            ? _templates.FirstOrDefault(item => item.Id == editingId)
            : null;
        _templateNameBox = new TextBox
        {
            Header = "模板名称",
            Text = editingTemplate?.Name ?? $"模板 {_templates.Count + 1}",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        host.Children.Add(_templateNameBox);

        _templateColorTypePicker = new ComboBox
        {
            Header = "当前颜色类型",
            ItemsSource = new[] { "填充", "线框", "图标" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        host.Children.Add(_templateColorTypePicker);

        var sampleGrid = new Grid { ColumnSpacing = 8 };
        sampleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        sampleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _templateSamplePreview = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(4),
            BorderBrush = new SolidColorBrush(EditorBorder),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(255, 40, 48, 60))
        };
        sampleGrid.Children.Add(_templateSamplePreview);
        _templateSampleText = new TextBlock
        {
            Text = "尚未取色",
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(_templateSampleText, 1);
        sampleGrid.Children.Add(_templateSampleText);
        host.Children.Add(sampleGrid);

        _templateSamplerButton = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new FontIcon { Glyph = "\uE71C", FontSize = 16 },
                    new TextBlock { Text = "吸管：点击画面记录当前类型颜色" }
                }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _templateSamplerButton.Click += (_, _) =>
        {
            _canvas.ArmTemplateColorSampler();
            _templateSamplerButton!.Content = "吸管已启用：点击画面取色（可连续取色）";
            SetStatus("吸管已启用，请点击画面上的像素取色。", false);
        };
        host.Children.Add(_templateSamplerButton);

        host.Children.Add(new TextBlock
        {
            Text = "已记录颜色",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        _templateDraftList = new StackPanel { Spacing = 4 };
        host.Children.Add(new ScrollViewer
        {
            Content = _templateDraftList,
            Height = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        _templateSaveButton = new Button
        {
            Content = _editingTemplateId is null ? "保存模板" : "保存修改",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = _draftTemplateEntries.Count > 0
        };
        _templateSaveButton.Click += async (_, _) => await SaveCurrentTemplateAsync();
        actions.Children.Add(_templateSaveButton);
        var clear = new Button { Content = "清空记录" };
        clear.Click += (_, _) =>
        {
            _draftTemplateEntries.Clear();
            RefreshTemplateDraftList();
            SetStatus("已清空当前模板记录。", false);
        };
        actions.Children.Add(clear);
        if (_editingTemplateId is not null)
        {
            _templateCancelEditButton = new Button { Content = "取消编辑" };
            _templateCancelEditButton.Click += (_, _) => CancelTemplateEdit();
            actions.Children.Add(_templateCancelEditButton);
        }
        host.Children.Add(actions);
        RefreshTemplateDraftList();
    }
}
