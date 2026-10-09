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

        if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary)
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
        CancelImportWorkflow();
        _importWorkflowCancellation = new CancellationTokenSource();
        var cancellationToken = _importWorkflowCancellation.Token;
        CancelPendingImportClick();
        ResetImportFloorDragSession(animateReturn: false);
        _draft = draft;
        if (draft.Id is null && draft.FloorPaths.Count == 0 && !IsBatchImport && !IsBatchOperation)
            await OfferMapTemplateAsync(draft);
        if (!IsCurrentImportWorkflow(draft, cancellationToken)) return;
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
                SharedStructure = draft.Floors.FirstOrDefault(item => item.Key == floor.Key)?.SharedStructure?.Clone(),
                ArtworkRegistration = draft.Floors.FirstOrDefault(item => item.Key == floor.Key)?.ArtworkRegistration?.Clone(),
                ArtworkCropProfile = GetImportArtworkCropProfile(draft, floor.Key),
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
        if (!IsCurrentImportWorkflow(draft, cancellationToken)) return;
        var enabledTagGroups = (await new MapTagStore().LoadAsync(catalog.Maps, catalog.Classes))
            .Where(group => group.IsEnabled
                && MapTagAuthorizationRules.IsAuthorized(group, draft.Class, catalog.Maps))
            .ToArray();
        if (!IsCurrentImportWorkflow(draft, cancellationToken)) return;

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
            if (!IsCurrentImportWorkflow(draft, cancellationToken)) return;
            CancelImportWorkflow();
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
        var continueButton = CreateActionButton("确认", IDVBuff.Appearance.ThemeButtonRole.Accent);
        continueButton.HorizontalAlignment = HorizontalAlignment.Center;
        continueButton.Width = 284;
        continueButton.IsEnabled = CanCommitImportFloors();
        continueButton.Click += async (_, _) =>
        {
            if (!IsCurrentImportWorkflow(draft, cancellationToken) || !CanCommitImportFloors()) return;
            var entries = _pendingImportFloors!.Select(entry => entry.Clone()).ToArray();
            PlayDetailTriggerFeedback(continueButton);
            CancelPendingImportClick();
            ResetImportFloorDragSession(animateReturn: false);
            continueButton.IsEnabled = false;
            floorScrollViewer.IsEnabled = false;
            try
            {
                await CommitImportFloorsWithStructuresAsync(draft, entries, cancellationToken);
                if (IsCurrentImportWorkflow(draft, cancellationToken)) ShowMarkerEditor();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Returning or navigating away owns the page; do not reopen it.
            }
            catch (Exception ex)
            {
                if (!IsCurrentImportWorkflow(draft, cancellationToken)) return;
                await new ContentDialog { XamlRoot = XamlRoot, Title = "结构底图尚未准备好",
                    Content = ex.Message, CloseButtonText = "返回检查" }.ShowThemedAsync(this);
            }
            finally
            {
                if (IsCurrentImportWorkflow(draft, cancellationToken))
                {
                    floorScrollViewer.IsEnabled = true;
                    continueButton.IsEnabled = CanCommitImportFloors();
                }
            }
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
        && _pendingImportFloors.All(entry => !string.IsNullOrWhiteSpace(entry.ImagePath)
            && (entry.SharedStructure is null && entry.StructureSourceDirectory.Length == 0
                || entry.ArtworkRegistration is not null));

    private async Task OfferMapTemplateAsync(MapDraft draft)
    {
        var combo = new ComboBox { PlaceholderText = "不使用模板", MinWidth = 300 };
        combo.Items.Add(new ComboBoxItem { Content = "不使用模板", Tag = null });
        var templates = MapTemplates.BuiltIn.Concat(await new MapTemplateStore().LoadAsync()).ToList();
        foreach (var availableTemplate in templates)
            combo.Items.Add(new ComboBoxItem { Content = availableTemplate.Name, Tag = availableTemplate });

        var memory = ShellLayoutMemory.Load();
        if (!string.IsNullOrWhiteSpace(memory.LastSelectedMapTemplateId))
        {
            var matchedItem = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => item.Tag is MapTemplate template && string.Equals(template.Id, memory.LastSelectedMapTemplateId, StringComparison.OrdinalIgnoreCase));
            combo.SelectedItem = matchedItem ?? combo.Items[0];
        }
        else
        {
            combo.SelectedIndex = 0;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "选择地图模板", Content = combo,
            PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary) return;

        var selectedTag = (combo.SelectedItem as ComboBoxItem)?.Tag;
        if (selectedTag is MapTemplate selectedTemplate)
        {
            memory.LastSelectedMapTemplateId = selectedTemplate.Id;
            memory.Save();
            draft.Floors = selectedTemplate.Floors.Select((floor, index) => new FloorDefinition
            {
                Key = floor.Key, DisplayName = floor.DisplayName, SortOrder = index + 1
            }).ToList();
        }
        else
        {
            memory.LastSelectedMapTemplateId = null;
            memory.Save();
        }
    }

    private UIElement CreateMapTagsEditor(MapDraft draft, IReadOnlyList<MapTagGroup> groups)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "编辑标签", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (groups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "当前没有已启用的标签组。", Foreground = FluentTheme.Brush(this, "TextFillColorSecondaryBrush") });
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

    /// <summary>Transfers the captured floor entries into their owning draft.</summary>
    private static void CommitPendingFloorsToDraft(MapDraft draft, IReadOnlyList<ImportFloorEntry> entries)
    {
        draft.Recognition.EnsureStandardAnchors();
        var profilesByKey = new Dictionary<string, FloorRecognitionProfile>(
            draft.Recognition.Floors,
            StringComparer.OrdinalIgnoreCase);
        var legacyFirstProfile = draft.Recognition.FirstFloor;
        var legacySecondProfile = draft.Recognition.SecondFloor;

        var previousFloors = draft.Floors.ToDictionary(floor => floor.Key, StringComparer.OrdinalIgnoreCase);
        var recognitionPaths = new Dictionary<string, string>(draft.FloorRecognitionSourcePaths, StringComparer.OrdinalIgnoreCase);
        var prebuiltPaths = new Dictionary<string, string>(draft.PrebuiltStructureLinePaths, StringComparer.OrdinalIgnoreCase);
        var sideFeaturePaths = new Dictionary<string, string>(draft.SideEntranceFeaturePaths, StringComparer.OrdinalIgnoreCase);
        var referenceProfiles = new Dictionary<string, FloorRecognitionProfile>(draft.SharedStructureSourceProfiles, StringComparer.OrdinalIgnoreCase);
        var portableGates = draft.PortableGates.Select(gate => gate.Clone()).ToArray();
        draft.FloorPaths.Clear();
        draft.FloorPreviewPaths.Clear();
        draft.FloorRecognitionSourcePaths.Clear();
        draft.PrebuiltStructureLinePaths.Clear();
        draft.SideEntranceFeaturePaths.Clear();
        draft.SharedStructureSourceProfiles.Clear();
        draft.PortableGates.Clear();
        draft.Floors.Clear();
        draft.FloorTwoPath = null;
        var profilesByNewKey = new Dictionary<string, FloorRecognitionProfile>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var sourceProfile = profilesByKey.GetValueOrDefault(entry.OriginalFloorKey)
                ?? (entry.OriginalFloorKey.Equals("1f", StringComparison.OrdinalIgnoreCase)
                    ? legacyFirstProfile
                    : entry.OriginalFloorKey.Equals("2f", StringComparison.OrdinalIgnoreCase)
                        ? legacySecondProfile
                        : null);
            var profile = sourceProfile?.Clone() ?? new FloorRecognitionProfile();
            profile.FloorKey = entry.FloorKey;
            profile.Floor = i == 0 ? MapFloor.First : MapFloor.Second;

            draft.FloorPaths[entry.FloorKey] = entry.ImagePath;
            draft.FloorPreviewPaths[entry.FloorKey] = entry.PreviewImagePath;
            if (recognitionPaths.TryGetValue(entry.OriginalFloorKey, out var recognitionPath))
                draft.FloorRecognitionSourcePaths[entry.FloorKey] = recognitionPath;
            if (prebuiltPaths.TryGetValue(entry.OriginalFloorKey, out var prebuiltPath))
                draft.PrebuiltStructureLinePaths[entry.FloorKey] = prebuiltPath;
            if (sideFeaturePaths.TryGetValue(entry.OriginalFloorKey, out var sideFeaturePath))
                draft.SideEntranceFeaturePaths[entry.FloorKey] = sideFeaturePath;
            if (referenceProfiles.TryGetValue(entry.OriginalFloorKey, out var referenceProfile))
            {
                var mappedProfile = referenceProfile.Clone();
                mappedProfile.FloorKey = entry.FloorKey;
                draft.SharedStructureSourceProfiles[entry.FloorKey] = mappedProfile;
            }
            foreach (var gate in portableGates.Where(gate => string.Equals(gate.FloorKey, entry.OriginalFloorKey, StringComparison.OrdinalIgnoreCase)))
            {
                var mapped = gate.Clone();
                mapped.FloorKey = entry.FloorKey;
                if (entry.FloorKey != entry.OriginalFloorKey)
                    mapped.Id = $"{entry.FloorKey}-{mapped.Id}";
                draft.PortableGates.Add(mapped);
            }
            draft.Floors.Add(new FloorDefinition
            {
                SharedStructure = previousFloors.GetValueOrDefault(entry.OriginalFloorKey)?.SharedStructure?.Clone(),
                ArtworkRegistration = entry.ArtworkRegistration?.Clone(),
                PrebuiltStructureLine = previousFloors.GetValueOrDefault(entry.OriginalFloorKey)?.PrebuiltStructureLine?.Clone(),
                Key = entry.FloorKey,
                DisplayName = entry.DisplayName,
                SortOrder = i + 1,
                MarkerKeys = MapFloorMarkerRules.Normalize(entry.MarkerKeys).ToList()
            });
            profilesByNewKey[entry.FloorKey] = profile;

            // 向后兼容：填充 FloorOnePath / FloorTwoPath
            if (i == 0)
            {
                draft.FloorOnePath = entry.ImagePath;
            }
            else if (i == 1)
            {
                draft.FloorTwoPath = entry.ImagePath;
            }
        }

        draft.Recognition.Floors = profilesByNewKey;
        draft.Recognition.NormalizeForFloors(draft.Floors);
    }
}
