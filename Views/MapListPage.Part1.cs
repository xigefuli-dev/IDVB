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
        var classPicker = CreateClassPicker();

        var publishButton = CreateActionButton(GetWebsiteActionText(), AccentBlue);
        _publishButton = publishButton;
        publishButton.IsEnabled = !_isPackageOperation && _loadedMaps.Count > 0;

        // The boundaries around the map-class controls share the remaining width,
        // so opening the navigation pane contracts those larger group gaps first.
        var mapActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        mapActions.Children.Add(importButton);
        mapActions.Children.Add(_editButton);
        mapActions.Children.Add(_deleteButton);
        mapActions.Children.Add(_variantButton);

        FrameworkElement[] actionElements =
        [
            mapActions,
            classPicker,
            publishButton
        ];
        for (var index = 0; index < actionElements.Length; index++)
        {
            actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(actionElements[index], index * 2);
            actionRow.Children.Add(actionElements[index]);

            if (index < actionElements.Length - 1)
            {
                actionRow.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                    MinWidth = 8
                });
            }
        }

        var teachingTip = CreateImportTeachingTip(importButton, publishButton);
        importButton.Click += (_, _) =>
        {
            if (_isPackageOperation)
                return;

            PlayDetailTriggerFeedback(importButton);
            teachingTip.IsOpen = !teachingTip.IsOpen;
        };
        var publishTeachingTip = CreatePublishTeachingTip(importButton, publishButton);
        publishButton.Click += (_, _) =>
        {
            if (_isPackageOperation || _loadedMaps.Count == 0)
                return;

            PlayDetailTriggerFeedback(publishButton);
            publishTeachingTip.IsOpen = !publishTeachingTip.IsOpen;
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
        _mapCardsSurface = mapSurface;
        scrollContent.Children.Add(mapSurface);

        // ── Root: overlay layout (Grid children stack in z-order) ──
        scrollContent.Margin = new Thickness(0, 0, 36, 0);
        var root = new Grid { Margin = new Thickness(36, 24, 0, 38) };
        ApplyViewportConstraint(root);

        // Bottom layer: full-height scroll area (cards only) — now safe from frozen bar
        var pageScroller = new ScrollViewer
        {
            Content = scrollContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(pageScroller);

        // Top layer: use the available width while retaining a small safe inset.
        var buttonBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 8, 12, 12),
            Child = actionRow
        };
        var buttonBarLayout = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 36, 0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
        };
        buttonBarLayout.Children.Add(buttonBar);
        root.Children.Add(buttonBarLayout);

        // TeachingTip must stay attached to the page's visual tree for its
        // complete lifetime. Opening an unattached tip crashes inside the
        // WinUI controls layer before managed exception handling can run.
        root.Children.Add(teachingTip);
        root.Children.Add(publishTeachingTip);

        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        UpdateSelectedCardVisuals();
    }

    private IReadOnlyList<MapRecord> GetVisibleMaps() => _loadedMaps
        .Where(map => string.Equals(map.Class, _selectedClass, StringComparison.OrdinalIgnoreCase))
        .Where(MatchesSelectedTagFilters)
        .OrderBy(map => map.SequenceNumber)
        .ToArray();

    private FrameworkElement CreateClassPicker()
    {
        var picker = new ComboBox
        {
            Width = 280,
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
                _selectedTagFilters.Clear();
                _selectedMapIds.Clear();
                _lastClickedMapId = null;
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
        remove.IsEnabled = _classes.Count > 1;
        remove.Click += async (_, _) => await ConfirmDeleteClassAsync(_selectedClass);

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
        controls.Children.Add(CreateFilterButton());
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
        var row = new Grid { MinWidth = 248 };
        row.Children.Add(new TextBlock
        {
            Text = className,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
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
        var nameBox = new TextBox { PlaceholderText = "输入新地图类名称", MinWidth = 220 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新建地图类",
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
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法创建地图类", exception.Message);
        }
    }

    private async Task ConfirmDeleteClassAsync(string className)
    {
        var count = _loadedMaps.Count(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"删除地图类“{className}”？",
            Content = $"将永久删除当前展示的地图类及其 {count} 张地图，此操作无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await _repository.DeleteClassAsync(className);
            if (!App.IsSafeMode)
                await App.Session.RefreshMapCacheAsync();
            if (string.Equals(_selectedClass, className, StringComparison.OrdinalIgnoreCase))
                _selectedClass = _classes.First(name => !string.Equals(name, className, StringComparison.OrdinalIgnoreCase));
            _selectedMapIds.Clear();
            _lastClickedMapId = null;
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("删除地图类失败", exception.Message);
        }
    }

}
