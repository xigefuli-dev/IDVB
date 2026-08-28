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
                confirmButton.IsEnabled = CanCommitImportFloors();
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
            confirmButton.IsEnabled = CanCommitImportFloors();
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

        var lowStructureToggle = new ToggleSwitch
        {
            Header = "低结构楼层",
            IsOn = MapFloorMarkerRules.Has(entry.MarkerKeys, MapFloorMarkerRules.LowStructure),
            Margin = new Thickness(10, 0, 10, 8)
        };
        lowStructureToggle.Toggled += (_, _) =>
        {
            entry.MarkerKeys = lowStructureToggle.IsOn
                ? MapFloorMarkerRules.Normalize(entry.MarkerKeys.Append(MapFloorMarkerRules.LowStructure)).ToList()
                : entry.MarkerKeys.Where(key => !string.Equals(key, MapFloorMarkerRules.LowStructure, StringComparison.Ordinal)).ToList();
        };

        var content = new Grid { RowSpacing = 10 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(imageFrame);
        Grid.SetRow(nameLabel, 1);
        content.Children.Add(nameLabel);
        Grid.SetRow(lowStructureToggle, 2);
        content.Children.Add(lowStructureToggle);

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
        card.PointerPressed += (_, e) =>
        {
            if (!IsImportCardInteractiveSource(e.OriginalSource))
                SelectImportFloorCard(entry.FloorKey, card);
        };
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
            if (IsImportCardInteractiveSource(e.OriginalSource))
                return;
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
                if (string.IsNullOrWhiteSpace(entry.ImagePath) || isSecondTap)
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

    private static bool IsImportCardInteractiveSource(object source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (current is ToggleSwitch or Button)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
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
