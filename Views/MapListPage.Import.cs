using IDVBuff.Features.Maps;
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

public sealed partial class MapListPage : UserControl
{
    private async Task<FloorIdentity?> ShowFloorIdentityDialogAsync(ImportFloorEntry? existing = null)
    {
        var idBox = new TextBox
        {
            Text = existing?.FloorKey ?? string.Empty,
            PlaceholderText = "例如：1f、b1、roof",
            Height = 36
        };
        var nameBox = new TextBox
        {
            Text = existing?.DisplayName ?? string.Empty,
            PlaceholderText = "例如：一楼、地下室",
            Height = 36
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "ID（只能包含英文字母和数字）",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
        });
        panel.Children.Add(idBox);
        panel.Children.Add(new TextBlock
        {
            Text = "名称",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 80, 80, 80))
        });
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设定楼层 ID 与名称",
            Content = panel,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false
        };

        void UpdateValidation()
        {
            var key = idBox.Text.Trim();
            var isValid = key.Length > 0 && key.All(char.IsAsciiLetterOrDigit);
            var isDuplicate = _pendingImportFloors?.Any(entry =>
                !ReferenceEquals(entry, existing)
                && string.Equals(entry.FloorKey, key, StringComparison.OrdinalIgnoreCase)) is true;
            dialog.IsPrimaryButtonEnabled = isValid && !isDuplicate;
        }

        idBox.TextChanged += (_, _) =>
        {
            var filtered = new string(idBox.Text.Where(char.IsAsciiLetterOrDigit).ToArray());
            if (filtered != idBox.Text)
            {
                var cursor = idBox.SelectionStart;
                idBox.Text = filtered;
                idBox.SelectionStart = Math.Min(cursor, filtered.Length);
            }
            UpdateValidation();
        };
        UpdateValidation();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var key = new string(idBox.Text.Where(c => char.IsAsciiLetterOrDigit(c)).ToArray());
        var displayName = nameBox.Text.Trim();

        if (key.Length == 0)
            return null;

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = key;

        return new FloorIdentity(key, displayName, MapFloorMarkerRules.Normalize(existing?.MarkerKeys));
    }

    private async Task ShowImportAsync(MapDraft draft)
    {
        CancelPendingImportClick();
        ResetImportFloorDragSession(animateReturn: false);
        _draft = draft;
        if (draft.Id is null && draft.FloorPaths.Count == 0 && !IsBatchImport && !IsBatchOperation)
            await OfferMapTemplateAsync(draft);
        _selectedImportFloorKey = null;
        _selectedImportFloorCard = null;
        _pendingImportFloors = draft.FloorPaths.Count > 0
            ? (draft.Floors.Count > 0
                ? draft.Floors.OrderBy(floor => floor.SortOrder)
                    .Where(floor => draft.FloorPaths.ContainsKey(floor.Key))
                    .Select(floor => new
                    {
                        floor.Key,
                        floor.DisplayName,
                        MarkerKeys = floor.MarkerKeys.ToArray()
                    })
                : draft.FloorPaths.Select(kvp => new
                {
                    Key = kvp.Key,
                    DisplayName = kvp.Key,
                    MarkerKeys = Array.Empty<string>()
                }))
                .Select(floor => new ImportFloorEntry
            {
                OriginalFloorKey = floor.Key,
                FloorKey = floor.Key,
                DisplayName = floor.DisplayName,
                MarkerKeys = MapFloorMarkerRules.Normalize(floor.MarkerKeys).ToList(),
                ImagePath = draft.FloorPaths[floor.Key],
                PreviewImagePath = draft.FloorPreviewPaths.TryGetValue(floor.Key, out var previewPath)
                    ? previewPath
                    : draft.FloorPaths[floor.Key]
            }).ToList()
            : draft.Floors.Select(floor => new ImportFloorEntry
            {
                OriginalFloorKey = floor.Key,
                FloorKey = floor.Key,
                DisplayName = floor.DisplayName,
                MarkerKeys = MapFloorMarkerRules.Normalize(floor.MarkerKeys).ToList()
            }).ToList();
        draft.Recognition.EnsureStandardAnchors();
        var catalog = await _repository.GetCatalogSnapshotAsync();
        var enabledTagGroups = (await new MapTagStore().LoadAsync(catalog.Maps, catalog.Classes))
            .Where(group => group.IsEnabled
                && MapTagAuthorizationRules.IsAuthorized(group, draft.Class, catalog.Maps))
            .ToArray();

        var root = new Grid
        {
            Margin = new Thickness(36, 31, 36, 38),
            MinHeight = 630,
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255))
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Header ──
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        titleBlock.Children.Add(CreateTitle("导入地图"));
        titleBlock.Children.Add(CreateDescription("为地图添加楼层图片。点击下方占位符开始，每层可设定自定义 ID 与名称。"));
        if (IsBatchImport)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量导入：第 {_batchDraftIndex + 1} / {_batchDrafts!.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        else if (IsBatchOperation && _batchQueue is not null)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量{(_batchType == BatchOperationType.Import ? "导入" : "编辑")}：第 {_batchQueueIndex + 1} / {_batchQueue.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        header.Children.Add(titleBlock);
        var backButton = CreateSecondaryButton("返回列表");
        backButton.Click += async (_, _) =>
        {
            CancelPendingImportClick();
            ResetImportFloorDragSession(animateReturn: false);
            ResetBatchImport();
            ResetBatchOperation();
            _pendingImportFloors = null;
            await ShowListAsync();
        };
        Grid.SetColumn(backButton, 1);
        header.Children.Add(backButton);
        root.Children.Add(header);

        // ── Floor area ──
        var floorAreaContainer = new Grid
        {
            Margin = new Thickness(0, 34, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var floorScrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        _importFloorScrollViewer = floorScrollViewer;
        floorAreaContainer.Children.Add(floorScrollViewer);
        Grid.SetRow(floorAreaContainer, 1);
        root.Children.Add(floorAreaContainer);

        // ── Confirm button ──
        var continueButton = CreateActionButton("确认", AccentBlue);
        continueButton.HorizontalAlignment = HorizontalAlignment.Center;
        continueButton.Width = 284;
        continueButton.IsEnabled = CanCommitImportFloors();
        continueButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(continueButton);
            CancelPendingImportClick();
            ResetImportFloorDragSession(animateReturn: false);
            CommitPendingFloorsToDraft();
            ShowMarkerEditor();
        };
        Grid.SetRow(continueButton, 2);
        root.Children.Add(continueButton);

        // ── Local: rebuild floor list UI ──
        void RenderFloorArea()
        {
            const int cardsPerRow = 4;
            var entries = _pendingImportFloors ?? [];
            var totalCards = entries.Count + 1; // existing floors plus the add tile
            var cardsGrid = new Grid
            {
                ColumnSpacing = 18,
                RowSpacing = 18,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 16)
            };
            _importFloorCardsGrid = cardsGrid;
            _importFloorCards.Clear();
            _importAddFloorCard = null;
            for (var column = 0; column < cardsPerRow; column++)
                cardsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < (totalCards + cardsPerRow - 1) / cardsPerRow; row++)
                cardsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var index = 0; index < entries.Count; index++)
            {
                var card = CreateImportFloorCard(entries[index], RenderFloorArea, continueButton);
                Grid.SetRow(card, index / cardsPerRow);
                Grid.SetColumn(card, index % cardsPerRow);
                cardsGrid.Children.Add(card);
            }

            var addCard = CreateAddFloorButton(RenderFloorArea, continueButton);
            Grid.SetRow(addCard, entries.Count / cardsPerRow);
            Grid.SetColumn(addCard, entries.Count % cardsPerRow);
            cardsGrid.Children.Add(addCard);
            _importAddFloorCard = addCard;

            var area = new StackPanel { Spacing = 28 };
            area.Children.Add(cardsGrid);
            area.Children.Add(CreateMapTagsEditor(draft, enabledTagGroups));
            floorScrollViewer.Content = area;
            UpdateImportFloorGridLayout();
        }

        RenderFloorArea();
        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        await Task.CompletedTask;
    }

    private bool CanCommitImportFloors() => _pendingImportFloors is { Count: > 0 }
        && _pendingImportFloors.All(entry => !string.IsNullOrWhiteSpace(entry.ImagePath));

    private async Task OfferMapTemplateAsync(MapDraft draft)
    {
        var combo = new ComboBox { PlaceholderText = "不使用模板", MinWidth = 300 };
        combo.Items.Add(new ComboBoxItem { Content = "不使用模板", Tag = null });
        var templates = MapTemplates.BuiltIn.Concat(await new MapTemplateStore().LoadAsync());
        foreach (var availableTemplate in templates)
            combo.Items.Add(new ComboBoxItem { Content = availableTemplate.Name, Tag = availableTemplate });
        combo.SelectedIndex = 0;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "选择地图模板", Content = combo,
            PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || (combo.SelectedItem as ComboBoxItem)?.Tag is not MapTemplate template) return;
        draft.Floors = template.Floors.Select((floor, index) => new FloorDefinition
        {
            Key = floor.Key, DisplayName = floor.DisplayName, SortOrder = index + 1
        }).ToList();
    }

    private UIElement CreateMapTagsEditor(MapDraft draft, IReadOnlyList<MapTagGroup> groups)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "编辑标签", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (groups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "当前没有已启用的标签组。", Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush") });
            return panel;
        }
        foreach (var group in groups)
        {
            var combo = new ComboBox { Header = group.Name, IsEditable = true, PlaceholderText = "可选", MinWidth = 280 };
            foreach (var tag in group.Tags) combo.Items.Add(tag);
            if (draft.Tags.TryGetValue(group.Id, out var selected))
            {
                var existing = group.Tags.FirstOrDefault(tag => string.Equals(tag, selected, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                    combo.SelectedItem = existing;
                else
                    combo.Text = selected;
            }
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string value)
                    draft.Tags[group.Id] = value;
            };
            combo.LostFocus += async (_, _) =>
            {
                var value = combo.Text.Trim();
                if (value.Length == 0) draft.Tags.Remove(group.Id);
                else
                {
                    draft.Tags[group.Id] = value;
                    if (!group.Tags.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        group.Tags.Add(value);
                        var all = (await new MapTagStore().LoadAsync()).ToList();
                        var stored = all.FirstOrDefault(item => item.Id == group.Id);
                        if (stored is not null && !stored.Tags.Contains(value, StringComparer.OrdinalIgnoreCase)) stored.Tags.Add(value);
                        var catalog = await _repository.GetCatalogSnapshotAsync();
                        await new MapTagStore().SaveAsync(all, catalog.Maps, catalog.Classes);
                    }
                }
            };
            panel.Children.Add(combo);
        }
        return panel;
    }

    /// <summary>Transfers <see cref="_pendingImportFloors"/> into <see cref="_draft"/>.</summary>
    private void CommitPendingFloorsToDraft()
    {
        if (_draft is null || _pendingImportFloors is null)
            return;

        _draft.Recognition.EnsureStandardAnchors();
        var profilesByKey = new Dictionary<string, FloorRecognitionProfile>(
            _draft.Recognition.Floors,
            StringComparer.OrdinalIgnoreCase);
        var legacyFirstProfile = _draft.Recognition.FirstFloor;
        var legacySecondProfile = _draft.Recognition.SecondFloor;

        _draft.FloorPaths.Clear();
        _draft.Floors.Clear();
        _draft.FloorTwoPath = null;
        var profilesByNewKey = new Dictionary<string, FloorRecognitionProfile>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < _pendingImportFloors.Count; i++)
        {
            var entry = _pendingImportFloors[i];
            var sourceProfile = profilesByKey.GetValueOrDefault(entry.OriginalFloorKey)
                ?? (entry.OriginalFloorKey.Equals("1f", StringComparison.OrdinalIgnoreCase)
                    ? legacyFirstProfile
                    : entry.OriginalFloorKey.Equals("2f", StringComparison.OrdinalIgnoreCase)
                        ? legacySecondProfile
                        : null);
            var profile = sourceProfile?.Clone() ?? new FloorRecognitionProfile();
            profile.FloorKey = entry.FloorKey;
            profile.Floor = i == 0 ? MapFloor.First : MapFloor.Second;

            _draft.FloorPaths[entry.FloorKey] = entry.ImagePath;
            _draft.Floors.Add(new FloorDefinition
            {
                Key = entry.FloorKey,
                DisplayName = entry.DisplayName,
                SortOrder = i + 1,
                MarkerKeys = MapFloorMarkerRules.Normalize(entry.MarkerKeys).ToList()
            });
            profilesByNewKey[entry.FloorKey] = profile;

            // 向后兼容：填充 FloorOnePath / FloorTwoPath
            if (i == 0)
            {
                _draft.FloorOnePath = entry.ImagePath;
            }
            else if (i == 1)
            {
                _draft.FloorTwoPath = entry.ImagePath;
            }
        }

        _draft.Recognition.Floors = profilesByNewKey;
        _draft.Recognition.NormalizeForFloors(_draft.Floors);
    }
}
