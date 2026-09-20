using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;
public sealed partial class SurveyProjectEditor
{

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
