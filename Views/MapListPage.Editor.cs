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
    private static void StartVisualTranslation(
        Microsoft.UI.Composition.Visual visual,
        Vector3 from,
        Vector3 to,
        TimeSpan duration)
    {
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0f, from);
        animation.InsertKeyFrame(1f, to, CreateMainEase(visual));
        animation.Duration = duration;
        visual.StartAnimation("Translation", animation);
    }

    private static SymbolIcon CreatePickerPlaceholder() => new()
    {
        Symbol = Symbol.Add,
        Width = 56,
        Height = 56,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button CreateImagePicker(Image preview, UIElement placeholder)
    {
        var content = new Grid();
        content.Children.Add(preview);
        content.Children.Add(placeholder);
        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190)),
            CornerRadius = new CornerRadius(7),
            Child = content
        };
        var button = new Button
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = surface
        };
        button.SizeChanged += (_, _) =>
        {
            if (button.ActualWidth > 0)
                button.Height = Math.Round(button.ActualWidth / 1.6);
        };
        return button;
    }

    private static void SetPickerPreview(Image image, UIElement placeholder, string? path)
    {
        var hasImage = MapRepository.IsSupportedImage(path) && File.Exists(path);
        image.Source = hasImage ? CreateBitmap(path!) : null;
        image.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        placeholder.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AttachImageDropTarget(UIElement target, Func<IReadOnlyList<string>, Task> onImagesDropped)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "释放以导入图片";
            e.Handled = true;
        };
        target.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
            try
            {
                var paths = await GetDroppedImagePathsAsync(e);
                if (paths.Count == 0)
                {
                    await ShowMessageAsync("没有可导入的图片", "请拖入有效的 PNG、JPG 或 JPEG 图片。");
                    return;
                }
                await onImagesDropped(paths);
            }
            catch (Exception exception)
            {
                await ShowMessageAsync("导入失败", exception.Message);
            }
        };
    }

    private static async Task<IReadOnlyList<string>> GetDroppedImagePathsAsync(DragEventArgs e)
    {
        var items = await e.DataView.GetStorageItemsAsync();
        var imageFiles = new List<StorageFile>();
        foreach (var item in items)
        {
            if (item is StorageFile file
                && MapRepository.IsSupportedImage(file.Path)
                && await IsReadableImageAsync(file))
            {
                imageFiles.Add(file);
            }
        }

        return imageFiles
            .OrderBy(file => file.Name, NaturalFileNameComparer.Instance)
            .Select(file => file.Path)
            .ToArray();
    }

    private static async Task<bool> IsReadableImageAsync(StorageFile file)
    {
        try
        {
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            _ = await BitmapDecoder.CreateAsync(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ImportFloorEntry
    {
        public string OriginalFloorKey { get; set; } = string.Empty;
        public string FloorKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string ImagePath { get; set; } = string.Empty;
        public string PreviewImagePath { get; set; } = string.Empty;
    }

    private sealed record FloorIdentity(string FloorKey, string DisplayName);

    private sealed class NaturalFileNameComparer : IComparer<string>
    {
        public static NaturalFileNameComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            var leftName = System.IO.Path.GetFileNameWithoutExtension(left ?? string.Empty);
            var rightName = System.IO.Path.GetFileNameWithoutExtension(right ?? string.Empty);
            var leftIndex = 0;
            var rightIndex = 0;
            while (leftIndex < leftName.Length && rightIndex < rightName.Length)
            {
                if (char.IsDigit(leftName[leftIndex]) && char.IsDigit(rightName[rightIndex]))
                {
                    var leftStart = leftIndex;
                    var rightStart = rightIndex;
                    while (leftIndex < leftName.Length && char.IsDigit(leftName[leftIndex]))
                        leftIndex++;
                    while (rightIndex < rightName.Length && char.IsDigit(rightName[rightIndex]))
                        rightIndex++;

                    var leftDigits = leftName[leftStart..leftIndex].TrimStart('0');
                    var rightDigits = rightName[rightStart..rightIndex].TrimStart('0');
                    leftDigits = leftDigits.Length == 0 ? "0" : leftDigits;
                    rightDigits = rightDigits.Length == 0 ? "0" : rightDigits;
                    var numberComparison = leftDigits.Length.CompareTo(rightDigits.Length);
                    if (numberComparison != 0)
                        return numberComparison;
                    numberComparison = string.Compare(leftDigits, rightDigits, StringComparison.Ordinal);
                    if (numberComparison != 0)
                        return numberComparison;
                    continue;
                }

                var characterComparison = char.ToUpperInvariant(leftName[leftIndex])
                    .CompareTo(char.ToUpperInvariant(rightName[rightIndex]));
                if (characterComparison != 0)
                    return characterComparison;
                leftIndex++;
                rightIndex++;
            }

            var lengthComparison = leftName.Length.CompareTo(rightName.Length);
            return lengthComparison != 0
                ? lengthComparison
                : StringComparer.CurrentCultureIgnoreCase.Compare(left, right);
        }
    }

    private async Task ImportDroppedImagesAsync(
        IReadOnlyList<string> paths,
        MapDraft draft,
        Action updatePickerPreviews)
    {
        if (paths.Count == 1)
        {
            AssignSingleDroppedImage(draft, paths[0]);
            updatePickerPreviews();
            return;
        }

        var groups = paths
            .Chunk(2)
            .Where(pair => pair.Length == 2)
            .Select(pair => pair.ToArray())
            .ToArray();
        var hasUnpairedImage = paths.Count % 2 != 0;
        if (groups.Length == 0)
        {
            AssignSingleDroppedImage(draft, paths[0]);
            updatePickerPreviews();
            return;
        }

        if (groups.Length > 1)
        {
            AssignDroppedPair(draft, groups[0]);
            _batchDrafts = [draft];
            _batchDrafts.AddRange(groups.Skip(1)
                .Select(pair => new MapDraft { FloorOnePath = pair[0], FloorTwoPath = pair[1] }));
            _batchDraftIndex = 0;
            _draft = _batchDrafts[0];
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            _pendingMarker = null;
            _dragStart = null;
            await ShowImportAsync(_draft);
            if (hasUnpairedImage)
            {
                await ShowMessageAsync("有一张图片未导入", "批量导入按文件名从小到大排序，每两张图片组成一组；最后一张未配对图片已跳过。");
            }
            return;
        }

        AssignDroppedPair(draft, groups[0]);
        updatePickerPreviews();
        if (hasUnpairedImage)
        {
            await ShowMessageAsync("有一张图片未导入", "批量导入按文件名从小到大排序，每两张图片组成一组；最后一张未配对图片已跳过。");
        }
    }

    private static void AssignSingleDroppedImage(MapDraft draft, string path)
    {
        if (!MapRepository.IsSupportedImage(draft.FloorOnePath) || !File.Exists(draft.FloorOnePath))
        {
            draft.FloorOnePath = path;
            ClearFloorAnchors(draft, "1f");
            return;
        }
        if (!MapRepository.IsSupportedImage(draft.FloorTwoPath) || !File.Exists(draft.FloorTwoPath))
        {
            draft.FloorTwoPath = path;
            ClearFloorAnchors(draft, "2f");
            return;
        }

        draft.FloorOnePath = path;
        ClearFloorAnchors(draft, "1f");
    }

    private static void AssignDroppedPair(MapDraft draft, IReadOnlyList<string> pair)
    {
        draft.FloorOnePath = pair[0];
        draft.FloorTwoPath = pair[1];
        ClearFloorAnchors(draft, "1f");
        ClearFloorAnchors(draft, "2f");
    }

    private bool IsBatchImport => _batchDrafts is { Count: > 1 };

    private void ResetBatchImport()
    {
        _batchDrafts = null;
        _batchDraftIndex = 0;
        _pendingImportFloors = null;
    }

    private void ResetMarkerEditorSession()
    {
        ResetSurveyProjectEditorSession();
        ResetModernMarkerEditorSession();
        DetachMarkerHostScroller();
        _activeAnchorId = null;
        _isSelectingRecognitionRegion = false;
        _isAnnotationPanelOpen = false;
        _activeAnnotationType = default;
        _pendingMarker = null;
        _dragStart = null;
        _panelDragStart = null;
        _panelPositionRatio = new Point(1d, 0d);
    }

    private bool TryAdvanceBatch()
    {
        if (!IsBatchImport || _batchDrafts is null || _batchDraftIndex + 1 >= _batchDrafts.Count)
            return false;

        _batchDraftIndex++;
        _draft = _batchDrafts[_batchDraftIndex];
        _activeFloorKey = "1f";
        _activeAnchorId = null;
        _isSelectingRecognitionRegion = false;
        _pendingMarker = null;
        _dragStart = null;
        return true;
    }

}
