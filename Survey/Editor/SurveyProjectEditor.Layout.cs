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

    private void BuildApplyTemplatePanel(StackPanel host)
    {
        _templatePicker = new ComboBox
        {
            Header = "选择模板",
            ItemsSource = _templates,
            DisplayMemberPath = nameof(SurveyColorTemplate.Name),
            SelectedIndex = _templates.Count == 0 ? -1 : 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _templatePicker.SelectionChanged += (_, _) =>
        {
            if (_templatePicker.SelectedItem is SurveyColorTemplate selected)
                SetStatus($"已选择模板“{selected.Name}”，点击图层即可套用；右侧多选时会批量处理全部选中图层。", false);
        };
        host.Children.Add(_templatePicker);
        host.Children.Add(new TextBlock
        {
            Text = "未多选时套用到点击的图层；右侧多选时，点击任意图层都会批量套用到全部选中图层（锁定图层也会处理）。一次操作只产生一个修订，可撤回和重做。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });
        var edit = new Button
        {
            Content = "编辑所选模板",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        edit.Click += (_, _) => BeginTemplateEdit();
        host.Children.Add(edit);
    }

    private void RefreshTemplateDraftList()
    {
        if (_templateDraftList is null)
            return;
        _templateDraftList.Children.Clear();
        foreach (var entry in _draftTemplateEntries)
        {
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(255, entry.R, entry.G, entry.B))
            });
            var description = new TextBlock
            {
                Text = $"{ToTemplateHex(entry.R, entry.G, entry.B)}  [{TemplateColorTypeName(entry.Type)}]",
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            var remove = new Button { Content = "×", MinWidth = 28, Padding = new Thickness(3) };
            var captured = entry;
            remove.Click += (_, _) =>
            {
                _draftTemplateEntries.Remove(captured);
                RefreshTemplateDraftList();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            _templateDraftList.Children.Add(row);
        }
        if (_draftTemplateEntries.Count == 0)
        {
            _templateDraftList.Children.Add(new TextBlock
            {
                Text = "尚未记录颜色。先选择颜色类型，再点击吸管并点选图层。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.65
            });
        }
        if (_templateSaveButton is not null)
            _templateSaveButton.IsEnabled = _draftTemplateEntries.Count > 0;
    }

    private SurveyTemplateColorType SelectedTemplateColorType() =>
        _templateColorTypePicker?.SelectedIndex switch
        {
            1 => SurveyTemplateColorType.Outline,
            2 => SurveyTemplateColorType.Icon,
            _ => SurveyTemplateColorType.Fill
        };

    private void SetTemplateSamplePreview(byte r, byte g, byte b)
    {
        if (_templateSamplePreview is not null)
            _templateSamplePreview.Background = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        if (_templateSampleText is not null)
            _templateSampleText.Text = ToTemplateHex(r, g, b);
    }

    private string ModernTemplateModeHint() => _templateMode == SurveyTemplateMode.Create
        ? "模板工具：新建模板模式。"
        : "模板工具：套用模板模式。";

    private static string ToTemplateHex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";

    private static string TemplateColorTypeName(SurveyTemplateColorType type) => type switch
    {
        SurveyTemplateColorType.Outline => "线框",
        SurveyTemplateColorType.Icon => "图标",
        _ => "填充"
    };

    private Flyout CreateVignettePropertiesFlyout()
    {
        var content = new StackPanel { Spacing = 10, Width = 270 };
        content.Children.Add(new TextBlock
        {
            Text = "晕影校正 / 反晕影",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "从图像中心向四角按椭圆归一化距离逐渐提亮，仅调整明度并保护高光。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        });

        var startValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var startHeader = new Grid();
        startHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        startHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        startHeader.Children.Add(new TextBlock { Text = "补偿起点" });
        Grid.SetColumn(startValue, 1);
        startHeader.Children.Add(startValue);
        content.Children.Add(startHeader);
        var start = new Slider
        {
            Minimum = 0d,
            Maximum = 100d,
            StepFrequency = 1d,
            Value = _vignetteStart * 100d
        };
        content.Children.Add(start);

        var strengthValue = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var strengthHeader = new Grid();
        strengthHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        strengthHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strengthHeader.Children.Add(new TextBlock { Text = "补偿强度（边缘最大提亮）" });
        Grid.SetColumn(strengthValue, 1);
        strengthHeader.Children.Add(strengthValue);
        content.Children.Add(strengthHeader);
        var strength = new Slider
        {
            Minimum = 0d,
            Maximum = 100d,
            StepFrequency = 1d,
            Value = _vignetteStrength * 100d
        };
        content.Children.Add(strength);

        var apply = new Button
        {
            Content = "应用到选中图层",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(apply);

        void RefreshValues()
        {
            _vignetteStart = Math.Clamp(start.Value / 100d, 0d, 1d);
            _vignetteStrength = Math.Clamp(strength.Value / 100d, 0d, 1d);
            startValue.Text = $"{Math.Round(start.Value)}%";
            strengthValue.Text = $"{Math.Round(strength.Value)}%";
        }
        start.ValueChanged += (_, _) => RefreshValues();
        strength.ValueChanged += (_, _) => RefreshValues();
        apply.Click += async (_, _) => await ApplyVignetteCorrectionToSelectionAsync();
        RefreshValues();
        return new Flyout { Content = content };
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
