using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class TagsAndTemplatesPage : UserControl
{
    private static readonly Color Transparent = Color.FromArgb(0, 0, 0, 0);
    private readonly MapTagStore _tagStore = new();
    private readonly MapTemplateStore _templateStore = new();
    private readonly MapRepository _repository = new();
    private readonly Grid _groupsGrid = new() { ColumnSpacing = 20, RowSpacing = 20 };
    private readonly Grid _templatesGrid = new() { ColumnSpacing = 20, RowSpacing = 20 };
    private List<MapTagGroup> _groups = [];
    private List<MapTemplate> _customTemplates = [];
    private IReadOnlyList<MapRecord> _maps = [];
    private IReadOnlyList<string> _classes = [];
    private int _columnCount = 3;

    public TagsAndTemplatesPage()
    {
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildPage()
        };
        Loaded += async (_, _) => await ReloadAsync();
        SizeChanged += (_, _) => UpdateResponsiveColumns();
    }

    private UIElement BuildPage()
    {
        var root = new StackPanel { Margin = new Thickness(44, 38, 44, 72) };
        root.Children.Add(new TextBlock { Text = "标签与模板", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "用标签整理地图特征，用模板快速建立楼层。",
            Margin = new Thickness(0, 18, 0, 30),
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        });
        root.Children.Add(CreateSectionHeader("标签系统", "新建标签组", AddGroupAsync));
        _groupsGrid.Margin = new Thickness(0, 18, 0, 30);
        root.Children.Add(_groupsGrid);
        root.Children.Add(new TextBlock
        {
            Text = "模板", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 18)
        });
        root.Children.Add(_templatesGrid);
        return root;
    }

    private static Grid CreateSectionHeader(string title, string actionText, Func<Task> action)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var button = new Button { Content = actionText, Padding = new Thickness(14, 7, 14, 7) };
        button.Click += async (_, _) => await action();
        Grid.SetColumn(button, 1);
        header.Children.Add(button);
        return header;
    }

    private async Task ReloadAsync()
    {
        _customTemplates = (await _templateStore.LoadAsync()).ToList();
        var catalog = await _repository.GetCatalogSnapshotAsync();
        _maps = catalog.Maps;
        _classes = catalog.Classes;
        _groups = (await _tagStore.LoadAsync(_maps, _classes)).ToList();
        UpdateResponsiveColumns(force: true);
    }

    private void UpdateResponsiveColumns(bool force = false)
    {
        var columns = ActualWidth switch { >= 1220 => 3, >= 800 => 2, _ => 1 };
        if (!force && columns == _columnCount) return;
        _columnCount = columns;
        RenderGroups();
        RenderTemplates();
    }

    private void RenderGroups()
    {
        ResetGrid(_groupsGrid, _columnCount);
        if (_groups.Count == 0)
        {
            var empty = CreateEmptyGroupCard();
            Grid.SetColumnSpan(empty, _columnCount);
            _groupsGrid.Children.Add(empty);
            return;
        }
        var columns = new StackPanel[_columnCount];
        for (var column = 0; column < _columnCount; column++)
        {
            columns[column] = new StackPanel { Spacing = 20 };
            Grid.SetColumn(columns[column], column);
            _groupsGrid.Children.Add(columns[column]);
        }
        for (var index = 0; index < _groups.Count; index++)
            columns[index % _columnCount].Children.Add(CreateGroupCard(_groups[index]));
    }

    private Border CreateGroupCard(MapTagGroup group)
    {
        var content = new StackPanel { Spacing = 14 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = group.Name, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        header.Children.Add(title);
        var enabled = new ToggleSwitch
        {
            IsOn = group.IsEnabled, OnContent = string.Empty, OffContent = string.Empty,
            Width = 44, MinWidth = 44,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var enabledState = new TextBlock
        {
            Text = group.IsEnabled ? "开" : "关",
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(enabled, "在地图编辑中显示");
        enabled.Toggled += async (_, _) =>
        {
            group.IsEnabled = enabled.IsOn;
            enabledState.Text = enabled.IsOn ? "开" : "关";
            await _tagStore.SaveAsync(_groups, _maps, _classes);
        };
        var authorizationButton = CreateAuthorizationButton(group);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { enabled, enabledState, authorizationButton }
        };
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        content.Children.Add(header);
        content.Children.Add(CreateTagTiles(group));
        return CreateSurface(content, 150);
    }

    private DropDownButton CreateAuthorizationButton(MapTagGroup group)
    {
        var button = new DropDownButton
        {
            Content = "···",
            Padding = new Thickness(8, 2, 8, 4),
            MinWidth = 40,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Flyout = CreateAuthorizationFlyout(group)
        };
        ToolTipService.SetToolTip(button, "设置可用模式");
        return button;
    }

    private Flyout CreateAuthorizationFlyout(MapTagGroup group)
    {
        var classList = new StackPanel { Spacing = 2, MinWidth = 220 };
        classList.Children.Add(new TextBlock
        {
            Text = "可用于哪些模式",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 4, 6)
        });

        if (_classes.Count == 0)
        {
            classList.Children.Add(new TextBlock
            {
                Text = "暂无 Class",
                Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
                Margin = new Thickness(4, 0, 4, 2)
            });
        }
        else
        {
            foreach (var className in _classes)
            {
                var isUsed = MapTagAuthorizationRules.IsUsedByClass(group, className, _maps);
                var checkBox = new CheckBox
                {
                    Content = className,
                    IsChecked = MapTagAuthorizationRules.IsAuthorized(group, className, _maps),
                    IsEnabled = !isUsed,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 5, 4, 5)
                };
                if (isUsed)
                    ToolTipService.SetToolTip(checkBox, "此 Class 已使用该标签组，不能取消授权");
                checkBox.Checked += async (_, _) => await SetClassAuthorizationAsync(group, className, true);
                checkBox.Unchecked += async (_, _) => await SetClassAuthorizationAsync(group, className, false);
                classList.Children.Add(checkBox);
            }
        }

        return new Flyout
        {
            Content = new ScrollViewer
            {
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = classList
            },
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };
    }

    private async Task SetClassAuthorizationAsync(MapTagGroup group, string className, bool authorized)
    {
        if (!authorized && MapTagAuthorizationRules.IsUsedByClass(group, className, _maps))
            return;

        var actualClass = _classes.FirstOrDefault(candidate =>
            string.Equals(candidate, className, StringComparison.OrdinalIgnoreCase)) ?? className;
        group.AuthorizedClasses ??= [];
        group.AuthorizedClasses.RemoveAll(candidate =>
            string.Equals(candidate, actualClass, StringComparison.OrdinalIgnoreCase));
        if (authorized)
            group.AuthorizedClasses.Add(actualClass);

        await _tagStore.SaveAsync(_groups, _maps, _classes);
    }

    private FlowPanel CreateTagTiles(MapTagGroup group)
    {
        var flow = new FlowPanel { HorizontalGap = 10, VerticalGap = 10 };
        for (var index = 0; index < group.Tags.Count; index++)
        {
            var tag = group.Tags[index];
            var usage = _maps.Count(map => map.Tags.TryGetValue(group.Id, out var value)
                && string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));
            var chip = new Grid
            {
                Height = 38, Padding = new Thickness(11, 0, 5, 0),
                MinWidth = 105, MaxWidth = 280,
                Background = FluentTheme.Brush("ControlFillColorSecondaryBrush"),
                CornerRadius = new CornerRadius(7)
            };
            chip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            chip.Children.Add(new TextBlock
            {
                Text = $"{tag}  ({usage})", VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var remove = new Button
            {
                Content = "×", Background = new SolidColorBrush(Transparent), BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0, 6, 1), MinWidth = 28, FontSize = 18,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            ToolTipService.SetToolTip(remove, "删除标签");
            remove.Click += async (_, _) => await RemoveTagAsync(group, tag, usage);
            Grid.SetColumn(remove, 1);
            chip.Children.Add(remove);
            flow.Children.Add(chip);
        }
        var addTile = new Button
        {
            Content = "+  新建标签", Height = 38,
            MinWidth = 132,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = FluentTheme.Brush("ControlFillColorSecondaryBrush"),
            BorderBrush = FluentTheme.Brush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7)
        };
        addTile.Click += async (_, _) => await AddTagAsync(group);
        flow.Children.Add(addTile);
        return flow;
    }

    private Border CreateEmptyGroupCard()
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, Spacing = 7,
                Children = { new SymbolIcon(Symbol.Add), new TextBlock { Text = "新建第一个标签组" } }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Transparent), BorderThickness = new Thickness(0)
        };
        button.Click += async (_, _) => await AddGroupAsync();
        return CreateSurface(button, 130);
    }

    private void RenderTemplates()
    {
        ResetGrid(_templatesGrid, _columnCount);
        var templates = MapTemplates.BuiltIn.Concat(_customTemplates).ToArray();
        for (var index = 0; index < templates.Length; index++)
            AddGridItem(_templatesGrid, CreateTemplateCard(templates[index]), index, _columnCount);
        AddGridItem(_templatesGrid, CreateNewTemplateCard(), templates.Length, _columnCount);
    }

    private Border CreateTemplateCard(MapTemplate template)
    {
        var builtIn = template.Id.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase);
        var panel = new StackPanel
        {
            Spacing = 7, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = template.Name, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                new TextBlock
                {
                    Text = string.Join("，", template.Floors.Select(floor => $"{floor.Key} / {floor.DisplayName}")),
                    Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = builtIn ? "内置模板" : "自定义模板", FontSize = 12,
                    Foreground = FluentTheme.Brush("TextFillColorTertiaryBrush")
                }
            }
        };
        return CreateSurface(panel, 108);
    }

    private Border CreateNewTemplateCard()
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, Spacing = 5,
                Children = { new SymbolIcon(Symbol.Add), new TextBlock { Text = "新建模板" } }
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Transparent), BorderThickness = new Thickness(0)
        };
        button.Click += async (_, _) => await AddTemplateAsync();
        return CreateSurface(button, 108);
    }

    private static Border CreateSurface(UIElement content, double minimumHeight) => new()
    {
        MinHeight = minimumHeight, Padding = new Thickness(18), CornerRadius = new CornerRadius(9),
        Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1), Child = content
    };

    private static void ResetGrid(Grid grid, int columns)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private static void AddGridItem(Grid grid, FrameworkElement element, int index, int columns)
    {
        var row = index / columns;
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, row);
        Grid.SetColumn(element, index % columns);
        grid.Children.Add(element);
    }

    private async Task AddGroupAsync()
    {
        var box = new TextBox { PlaceholderText = "例如：门方位" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "新建标签组", Content = box,
            PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length == 0 || _groups.Any(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase))) return;
        _groups.Add(new MapTagGroup { Name = name });
        await _tagStore.SaveAsync(_groups, _maps, _classes);
        RenderGroups();
    }

    private async Task AddTagAsync(MapTagGroup group)
    {
        var box = new TextBox { PlaceholderText = "输入标签" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"添加到“{group.Name}”", Content = box,
            PrimaryButtonText = "添加", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var value = box.Text.Trim();
        if (value.Length == 0 || group.Tags.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        group.Tags.Add(value);
        await _tagStore.SaveAsync(_groups, _maps, _classes);
        RenderGroups();
    }

    private async Task RemoveTagAsync(MapTagGroup group, string tag, int usage)
    {
        if (usage > 0)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "删除正在使用的标签？",
                Content = $"仍有 {usage} 组地图使用“{tag}”。删除后地图数据会保留该值，但不会再作为可选标签显示。",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        group.Tags.RemoveAll(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));
        await _tagStore.SaveAsync(_groups, _maps, _classes);
        RenderGroups();
    }
}

internal sealed class FlowPanel : Panel
{
    public double HorizontalGap { get; set; } = 8;
    public double VerticalGap { get; set; } = 8;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var maximumWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(maximumWidth, double.PositiveInfinity));
            var width = Math.Min(child.DesiredSize.Width, maximumWidth);
            if (x > 0 && x + HorizontalGap + width > maximumWidth)
            {
                x = 0;
                y += rowHeight + VerticalGap;
                rowHeight = 0;
            }
            if (x > 0) x += HorizontalGap;
            x += width;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        return new Windows.Foundation.Size(
            double.IsInfinity(availableSize.Width) ? x : availableSize.Width,
            y + rowHeight);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
    {
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        foreach (var child in Children)
        {
            var width = Math.Min(child.DesiredSize.Width, finalSize.Width);
            if (x > 0 && x + HorizontalGap + width > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalGap;
                rowHeight = 0;
            }
            if (x > 0) x += HorizontalGap;
            child.Arrange(new Windows.Foundation.Rect(x, y, width, child.DesiredSize.Height));
            x += width;
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
        }
        return finalSize;
    }
}
