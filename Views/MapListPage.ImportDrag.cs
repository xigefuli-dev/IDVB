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
    private void StartImportFloorHoldTimer(Action onActivated)
    {
        StopImportFloorHoldTimer();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_importHoldTimer, timer))
                return;
            _importHoldTimer = null;
            onActivated();
        };
        _importHoldTimer = timer;
        timer.Start();
    }

    private void StopImportFloorHoldTimer()
    {
        _importHoldTimer?.Stop();
        _importHoldTimer = null;
    }

    private void BeginImportFloorDrag(ImportFloorEntry entry, Border card)
    {
        if (!ReferenceEquals(_draggedImportFloor, entry)
            || !ReferenceEquals(_draggedImportFloorCard, card))
            return;

        StopImportFloorHoldTimer();
        StopImportFloorDropSettleTimer();
        CancelPendingImportClick();
        _isDraggingImportFloor = true;
        Canvas.SetZIndex(card, 100);
        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.StopAnimation("Translation");
        _importDragVisualTranslation = Vector3.Zero;
        StartVisualTranslation(
            visual,
            Vector3.Zero,
            Vector3.Zero,
            TimeSpan.FromMilliseconds(1));
        PlayHoverFeedback(card, 1.04f, TimeSpan.FromMilliseconds(130));
        StartImportFloorFollowTimer();
        UpdateImportDragFrame();
    }

    private void StartImportFloorFollowTimer()
    {
        StopImportFloorFollowTimer();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => UpdateImportDragFrame();
        _importDragFollowTimer = timer;
        timer.Start();
    }

    private void StopImportFloorFollowTimer()
    {
        _importDragFollowTimer?.Stop();
        _importDragFollowTimer = null;
    }

    private void UpdateImportDragPointer(PointerRoutedEventArgs e, UIElement fallbackTarget)
    {
        _importDragPointerPosition = _importFloorCardsGrid is { } grid
            ? e.GetCurrentPoint(grid).Position
            : e.GetCurrentPoint(fallbackTarget).Position;
        if (_importFloorScrollViewer is { } scrollViewer)
            _importDragPointerInScrollViewer = e.GetCurrentPoint(scrollViewer).Position;
    }

    private Point GetImportDragPointerPosition()
    {
        if (_importFloorScrollViewer is { } scrollViewer
            && _importFloorCardsGrid is { } grid)
        {
            return scrollViewer.TransformToVisual(grid)
                .TransformPoint(_importDragPointerInScrollViewer);
        }
        return _importDragPointerPosition;
    }

    private void UpdateImportDragFrame()
    {
        if (!_isDraggingImportFloor
            || _draggedImportFloor is not { } entry
            || _draggedImportFloorCard is not { } card
            || _importFloorCardsGrid is not { } grid)
            return;

        UpdateImportFloorAutoScroll();
        var pointerPosition = GetImportDragPointerPosition();
        ReorderImportFloorCard(entry, pointerPosition);

        var layoutOrigin = card.TransformToVisual(grid).TransformPoint(new Point(0, 0));
        var targetTranslation = new Vector3(
            (float)(pointerPosition.X - layoutOrigin.X - _importDragStartPoint.X),
            (float)(pointerPosition.Y - layoutOrigin.Y - _importDragStartPoint.Y),
            0);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        var nextTranslation = Vector3.Lerp(
            _importDragVisualTranslation,
            targetTranslation,
            0.36f);
        StartVisualTranslation(
            visual,
            _importDragVisualTranslation,
            nextTranslation,
            TimeSpan.FromMilliseconds(16));
        _importDragVisualTranslation = nextTranslation;
    }

    private void UpdateImportFloorAutoScroll()
    {
        if (_importFloorScrollViewer is not { } scrollViewer || scrollViewer.ActualHeight <= 0)
            return;

        const double edgeThreshold = 48d;
        const double pixelsPerTick = 18d;
        var pointerY = _importDragPointerInScrollViewer.Y;
        var delta = pointerY < edgeThreshold
            ? -pixelsPerTick * (1d - (pointerY / edgeThreshold))
            : pointerY > scrollViewer.ActualHeight - edgeThreshold
                ? pixelsPerTick * (1d - ((scrollViewer.ActualHeight - pointerY) / edgeThreshold))
                : 0d;
        if (Math.Abs(delta) < 0.01d)
            return;

        var nextOffset = Math.Clamp(
            scrollViewer.VerticalOffset + delta,
            0d,
            scrollViewer.ScrollableHeight);
        scrollViewer.ChangeView(null, nextOffset, null, disableAnimation: true);
    }

    private void ClearImportFloorDragCandidate()
    {
        _isDraggingImportFloor = false;
        _draggedImportFloor = null;
        _draggedImportFloorCard = null;
    }

    private void ResetImportFloorDragSession(bool animateReturn)
    {
        StopImportFloorHoldTimer();
        StopImportFloorFollowTimer();
        StopImportFloorDropSettleTimer();
        var card = _draggedImportFloorCard;
        ClearImportFloorDragCandidate();
        if (card is null)
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(card, true);
        var visual = ElementCompositionPreview.GetElementVisual(card);
        visual.StopAnimation("Translation");
        if (animateReturn)
        {
            StartVisualTranslation(
                visual,
                _importDragVisualTranslation,
                Vector3.Zero,
                TimeSpan.FromMilliseconds(180));
            ScheduleImportFloorDropSettle(card);
        }
        else
        {
            StartVisualTranslation(
                visual,
                Vector3.Zero,
                Vector3.Zero,
                TimeSpan.FromMilliseconds(1));
            Canvas.SetZIndex(card, 0);
        }
        _importDragVisualTranslation = Vector3.Zero;
        PlayHoverFeedback(card, 1f, TimeSpan.FromMilliseconds(160));
    }

    private void ScheduleImportFloorDropSettle(Border card)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(190);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_importDropSettleTimer, timer))
                return;
            _importDropSettleTimer = null;
            Canvas.SetZIndex(card, 0);
        };
        _importDropSettleTimer = timer;
        timer.Start();
    }

    private void StopImportFloorDropSettleTimer()
    {
        _importDropSettleTimer?.Stop();
        _importDropSettleTimer = null;
    }

    private void QueueImportFloorClick(
        ImportFloorEntry entry,
        Action onChanged,
        Button confirmButton)
    {
        if (_pendingImportClickEntry is not null
            && ReferenceEquals(_pendingImportClickEntry, entry)
            && _importClickTimer is not null)
        {
            CancelPendingImportClick();
            _ = ReplaceImportFloorImageAsync(entry, onChanged, confirmButton);
            return;
        }

        if (_pendingImportClickEntry is { } previous)
        {
            CancelPendingImportClick();
            _ = EditImportFloorAsync(previous, onChanged);
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(280);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_importClickTimer, timer))
                return;
            _importClickTimer = null;
            _pendingImportClickEntry = null;
            _ = EditImportFloorAsync(entry, onChanged);
        };
        _pendingImportClickEntry = entry;
        _importClickTimer = timer;
        timer.Start();
    }

    private void CancelPendingImportClick()
    {
        _importClickTimer?.Stop();
        _importClickTimer = null;
        _pendingImportClickEntry = null;
    }

    private async Task EditImportFloorAsync(ImportFloorEntry entry, Action onChanged)
    {
        if (_pendingImportFloors?.Contains(entry) is not true)
            return;

        var identity = await ShowFloorIdentityDialogAsync(entry);
        if (identity is null)
            return;

        var oldKey = entry.FloorKey;
        entry.FloorKey = identity.FloorKey;
        entry.DisplayName = identity.DisplayName;
        if (string.Equals(_selectedImportFloorKey, oldKey, StringComparison.OrdinalIgnoreCase))
            _selectedImportFloorKey = entry.FloorKey;
        onChanged();
    }

    private async Task ReplaceImportFloorImageAsync(
        ImportFloorEntry entry,
        Action onChanged,
        Button confirmButton)
    {
        if (_pendingImportFloors?.Contains(entry) is not true)
            return;

        var selectedPath = await PickImageAsync("替换楼层图片");
        if (selectedPath is null)
            return;

        entry.ImagePath = selectedPath;
        entry.PreviewImagePath = selectedPath;
        confirmButton.IsEnabled = _pendingImportFloors.Count > 0;
        onChanged();
    }

    private void ReorderImportFloorCard(ImportFloorEntry entry, Point pointerPosition)
    {
        if (_pendingImportFloors is not { Count: > 1 } entries
            || _importFloorCardsGrid is not { } grid)
            return;

        var currentIndex = entries.IndexOf(entry);
        if (currentIndex < 0)
            return;

        var remaining = entries.Where(candidate => !ReferenceEquals(candidate, entry)).ToList();
        var insertIndex = Math.Clamp(currentIndex, 0, remaining.Count);
        if (_importAddFloorCard is { } addCard
            && TryGetImportFloorCardRect(addCard, grid, out var addCardRect)
            && addCardRect.Contains(pointerPosition))
        {
            insertIndex = remaining.Count;
        }
        for (var index = 0; index < remaining.Count; index++)
        {
            if (!_importFloorCards.TryGetValue(remaining[index], out var targetCard))
                continue;

            if (!TryGetImportFloorCardRect(targetCard, grid, out var targetRect))
                continue;
            var midpointY = targetRect.Top + (targetRect.Height / 2d);
            if (pointerPosition.X >= targetRect.Left
                && pointerPosition.X <= targetRect.Right
                && pointerPosition.Y >= targetRect.Top
                && pointerPosition.Y <= targetRect.Bottom)
            {
                insertIndex = pointerPosition.Y < midpointY ? index : index + 1;
                break;
            }
        }

        if (pointerPosition.Y > grid.ActualHeight)
            insertIndex = remaining.Count;
        else if (pointerPosition.Y < 0d)
            insertIndex = 0;

        var projected = FloorOrderProjection.MoveToInsertion(entries, entry, insertIndex);
        if (entries.SequenceEqual(projected))
            return;

        entries.Clear();
        entries.AddRange(projected);
        UpdateImportFloorGridLayout(animateReflow: true);
    }

    private static bool TryGetImportFloorCardRect(UIElement element, UIElement relativeTo, out Rect rect)
    {
        if (element is not FrameworkElement frameworkElement
            || frameworkElement.ActualWidth <= 0d
            || frameworkElement.ActualHeight <= 0d)
        {
            rect = default;
            return false;
        }

        var origin = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        rect = new Rect(origin.X, origin.Y, frameworkElement.ActualWidth, frameworkElement.ActualHeight);
        return true;
    }

    private void UpdateImportFloorGridLayout(bool animateReflow = false)
    {
        if (_importFloorCardsGrid is not { } grid || _pendingImportFloors is not { } entries)
            return;

        var previousPositions = animateReflow
            ? CaptureImportFloorLayoutPositions(grid)
            : null;
        const int cardsPerRow = 4;
        var totalCards = entries.Count + 1;
        grid.RowDefinitions.Clear();
        for (var row = 0; row < (totalCards + cardsPerRow - 1) / cardsPerRow; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var index = 0; index < entries.Count; index++)
        {
            if (_importFloorCards.TryGetValue(entries[index], out var card))
            {
                Grid.SetRow(card, index / cardsPerRow);
                Grid.SetColumn(card, index % cardsPerRow);
            }
        }

        if (_importAddFloorCard is not null)
        {
            Grid.SetRow(_importAddFloorCard, entries.Count / cardsPerRow);
            Grid.SetColumn(_importAddFloorCard, entries.Count % cardsPerRow);
        }

        if (previousPositions is { Count: > 0 })
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_importFloorCardsGrid, grid))
                    return;
                AnimateImportFloorReflow(grid, previousPositions);
            });
        }
    }

    private Dictionary<UIElement, Point> CaptureImportFloorLayoutPositions(Grid grid)
    {
        var positions = new Dictionary<UIElement, Point>();
        foreach (var card in _importFloorCards.Values)
        {
            if (TryGetImportFloorCardRect(card, grid, out var rect))
                positions[card] = new Point(rect.X, rect.Y);
        }
        if (_importAddFloorCard is { } addCard
            && TryGetImportFloorCardRect(addCard, grid, out var addRect))
        {
            positions[addCard] = new Point(addRect.X, addRect.Y);
        }
        return positions;
    }

    private void AnimateImportFloorReflow(Grid grid, IReadOnlyDictionary<UIElement, Point> previousPositions)
    {
        foreach (var (element, previousPosition) in previousPositions)
        {
            if (ReferenceEquals(element, _draggedImportFloorCard)
                || !TryGetImportFloorCardRect(element, grid, out var currentRect))
                continue;

            var delta = new Vector3(
                (float)(previousPosition.X - currentRect.X),
                (float)(previousPosition.Y - currentRect.Y),
                0);
            if (delta.LengthSquared() < 0.01f)
                continue;

            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
            var visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Translation");
            StartVisualTranslation(
                visual,
                delta,
                Vector3.Zero,
                TimeSpan.FromMilliseconds(170));
        }
    }
}
