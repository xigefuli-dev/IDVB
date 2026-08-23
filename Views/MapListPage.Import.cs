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
        var lowStructureToggle = new ToggleSwitch
        {
            Header = "低结构楼层",
            OnContent = "开启",
            OffContent = "关闭",
            IsOn = existing is not null
                && MapFloorMarkerRules.Has(
                    existing.MarkerKeys,
                    MapFloorMarkerRules.LowStructure)
        };
        var lowStructureDescription = new TextBlock
        {
            Text = "结构稀疏时使用独立的全尺度搜索通道。",
            FontSize = 12,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
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
        panel.Children.Add(lowStructureToggle);
        panel.Children.Add(lowStructureDescription);

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

        var markerKeys = MapFloorMarkerRules.Normalize(existing?.MarkerKeys);
        markerKeys = lowStructureToggle.IsOn
            ? MapFloorMarkerRules.Normalize(markerKeys.Append(MapFloorMarkerRules.LowStructure))
            : markerKeys
                .Where(key => !string.Equals(
                    key,
                    MapFloorMarkerRules.LowStructure,
                    StringComparison.Ordinal))
                .ToArray();
        return new FloorIdentity(key, displayName, markerKeys);
    }

    private async Task ShowImportAsync(MapDraft draft)
    {
        CancelPendingImportClick();
        ResetImportFloorDragSession(animateReturn: false);
        _draft = draft;
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
            : [];
        draft.Recognition.EnsureStandardAnchors();

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
        continueButton.IsEnabled = _pendingImportFloors.Count > 0;
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

            floorScrollViewer.Content = cardsGrid;
            UpdateImportFloorGridLayout();
        }

        RenderFloorArea();
        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        await Task.CompletedTask;
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

    private Button CreateAddFloorButton(Action onChanged, Button confirmButton)
    {
        var placeholderIcon = new SymbolIcon
        {
            Symbol = Symbol.Add,
            Width = 56,
            Height = 56,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var placeholderLabel = new TextBlock
        {
            Text = "添加楼层图片",
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var iconSurface = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 0
        };
        iconSurface.Children.Add(placeholderIcon);
        iconSurface.Children.Add(placeholderLabel);

        // 图片占位区域 — 匹配 CreateImagePicker 的结构
        var imagePlaceholder = new Border
        {
            Background = FluentTheme.Brush("ControlFillColorSecondaryBrush"),
            CornerRadius = new CornerRadius(7),
            Child = iconSurface,
            Height = 205,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // 名称占位区域（与楼层卡片的名称对齐）
        var namePlaceholder = new TextBlock
        {
            Text = " ",
            FontSize = 14,
            Height = 24,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var content = new Grid { RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(imagePlaceholder);
        Grid.SetRow(namePlaceholder, 1);
        content.Children.Add(namePlaceholder);

        // 外层卡片 — 背景和圆角在 Border 上，不在 Button 上
        var cardSurface = new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content
        };

        var card = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = cardSurface,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AttachCardInteractionFeedback(card);

        card.Click += async (_, _) =>
        {
            card.IsEnabled = false;
            try
            {
                var selectedPath = await PickImageAsync("选择楼层地图");
                if (selectedPath is null)
                    return;

                var identity = await ShowFloorIdentityDialogAsync();
                if (identity is null)
                    return;

                var entry = new ImportFloorEntry
                {
                    OriginalFloorKey = identity.FloorKey,
                    FloorKey = identity.FloorKey,
                    DisplayName = identity.DisplayName,
                    MarkerKeys = identity.MarkerKeys.ToList(),
                    ImagePath = selectedPath,
                    PreviewImagePath = selectedPath
                };
                _pendingImportFloors ??= [];
                _pendingImportFloors.Add(entry);
                _selectedImportFloorKey = entry.FloorKey;
                confirmButton.IsEnabled = _pendingImportFloors.Count > 0;
                onChanged();
            }
            finally
            {
                card.IsEnabled = true;
            }
        };

        return card;
    }

    private Border CreateImportFloorCard(ImportFloorEntry entry, Action onChanged, Button confirmButton)
    {
        var image = new Image { Stretch = Stretch.UniformToFill };
        var previewPath = MapRepository.IsSupportedImage(entry.PreviewImagePath)
            && File.Exists(entry.PreviewImagePath)
            ? entry.PreviewImagePath
            : entry.ImagePath;
        if (MapRepository.IsSupportedImage(previewPath) && File.Exists(previewPath))
            image.Source = CreateBitmap(previewPath);

        var deleteButton = new Button
        {
            Content = "✕",
            Background = new SolidColorBrush(Color.FromArgb(200, 40, 40, 40)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            FontSize = 12,
            CornerRadius = new CornerRadius(14),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0)
        };
        deleteButton.Click += (_, _) =>
        {
            _pendingImportFloors?.Remove(entry);
            if (_selectedImportFloorKey == entry.FloorKey)
            {
                _selectedImportFloorKey = null;
                _selectedImportFloorCard = null;
            }
            confirmButton.IsEnabled = _pendingImportFloors?.Count > 0;
            onChanged();
        };
        deleteButton.PointerPressed += (_, e) => e.Handled = true;
        deleteButton.PointerMoved += (_, e) => e.Handled = true;
        deleteButton.PointerReleased += (_, e) => e.Handled = true;

        var imageSurface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = image
        };

        var overlay = new Grid();
        overlay.Children.Add(imageSurface);
        overlay.Children.Add(deleteButton);

        var imageFrame = new Border
        {
            CornerRadius = new CornerRadius(7),
            Child = overlay,
            MinHeight = 175,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        imageFrame.SizeChanged += (_, _) =>
        {
            if (imageFrame.ActualWidth <= 0)
                return;
            imageFrame.Height = Math.Max(175, Math.Round(imageFrame.ActualWidth / 1.6));
            imageFrame.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Rect(0, 0, imageFrame.ActualWidth, imageFrame.ActualHeight)
            };
        };

        var nameLabel = new TextBlock
        {
            Text = entry.DisplayName,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };

        var content = new Grid { RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(imageFrame);
        Grid.SetRow(nameLabel, 1);
        content.Children.Add(nameLabel);

        var card = new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = new SolidColorBrush(
                _selectedImportFloorKey == entry.FloorKey ? AccentBlue : Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = content,
        };
        card.PointerPressed += (_, _) => SelectImportFloorCard(entry.FloorKey, card);
        AttachImportFloorCardInteraction(card, entry, onChanged, confirmButton);
        _importFloorCards[entry] = card;
        return card;
    }

    private void SelectImportFloorCard(string floorKey, Border card)
    {
        _selectedImportFloorKey = floorKey;
        if (_selectedImportFloorCard is not null && _selectedImportFloorCard != card)
            _selectedImportFloorCard.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        _selectedImportFloorCard = card;
        card.BorderBrush = new SolidColorBrush(AccentBlue);
    }

    private void AttachImportFloorCardInteraction(
        Border card,
        ImportFloorEntry entry,
        Action onChanged,
        Button confirmButton)
    {
        var isPressed = false;
        var pressCanceled = false;
        var isReleasingCapture = false;
        var isSecondTap = false;

        card.PointerEntered += (_, _) =>
        {
            if (!isPressed)
                PlayHoverFeedback(card, 1.01f, TimeSpan.FromMilliseconds(150));
        };
        card.PointerExited += (_, _) =>
        {
            if (isPressed)
                pressCanceled = true;
            if (!isPressed || !_isDraggingImportFloor)
                PlayHoverFeedback(card, 1f, TimeSpan.FromMilliseconds(100));
        };
        card.PointerPressed += (_, e) =>
        {
            isPressed = true;
            pressCanceled = false;
            isSecondTap = BeginImportFloorPointerPress(entry, onChanged);
            _draggedImportFloor = entry;
            _draggedImportFloorCard = card;
            _importDragStartPoint = e.GetCurrentPoint(card).Position;
            _isDraggingImportFloor = false;
            SelectImportFloorCard(entry.FloorKey, card);
            card.CapturePointer(e.Pointer);
            UpdateImportDragPointer(e, card);
            PlayHoverFeedback(card, 0.975f, TimeSpan.FromMilliseconds(80));
            StartImportFloorHoldTimer(() =>
            {
                if (!isPressed || !ReferenceEquals(_draggedImportFloor, entry))
                    return;
                pressCanceled = true;
                BeginImportFloorDrag(entry, card);
            });
        };
        card.PointerMoved += (_, e) =>
        {
            if (!isPressed || _draggedImportFloor != entry)
                return;

            UpdateImportDragPointer(e, card);
            if (_isDraggingImportFloor)
                UpdateImportDragFrame();
        };
        card.PointerReleased += (_, e) =>
        {
            if (!isPressed)
                return;

            isPressed = false;
            StopImportFloorHoldTimer();
            isReleasingCapture = true;
            card.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;

            var wasDragging = _isDraggingImportFloor;
            if (wasDragging)
                ResetImportFloorDragSession(animateReturn: true);
            else
            {
                ClearImportFloorDragCandidate();
                PlayHoverFeedback(card, 1f, TimeSpan.FromMilliseconds(110));
            }

            if (!wasDragging && !pressCanceled)
            {
                if (isSecondTap)
                    _ = ReplaceImportFloorImageAsync(entry, onChanged, confirmButton);
                else
                    QueueImportFloorClick(entry, onChanged, confirmButton);
            }
        };
        card.PointerCanceled += (_, e) =>
        {
            isPressed = false;
            pressCanceled = true;
            StopImportFloorHoldTimer();
            isReleasingCapture = true;
            card.ReleasePointerCapture(e.Pointer);
            isReleasingCapture = false;
            ResetImportFloorDragSession(animateReturn: _isDraggingImportFloor);
        };
        card.PointerCaptureLost += (_, _) =>
        {
            if (isReleasingCapture)
                return;
            isPressed = false;
            pressCanceled = true;
            StopImportFloorHoldTimer();
            ResetImportFloorDragSession(animateReturn: _isDraggingImportFloor);
        };
    }

    private bool BeginImportFloorPointerPress(ImportFloorEntry entry, Action onChanged)
    {
        if (_pendingImportClickEntry is not null
            && ReferenceEquals(_pendingImportClickEntry, entry)
            && _importClickTimer is not null)
        {
            CancelPendingImportClick();
            return true;
        }

        if (_pendingImportClickEntry is { } previous)
        {
            CancelPendingImportClick();
            _ = EditImportFloorAsync(previous, onChanged);
        }

        return false;
    }
}
