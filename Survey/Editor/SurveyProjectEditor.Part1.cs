using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Survey.Editor.WinUI;
public sealed partial class SurveyProjectEditor : UserControl, IDisposable
{

    private async void Canvas_MaskStrokeCommitted(object? sender, SurveyMaskStrokeEventArgs e)
    {
        if (_disposed || _session.Snapshot is not { } snapshot)
            return;
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase));
        if (floor is null)
            return;
        Guid[] targets;
        if (_eraseMode == SurveyEraseMode.Eraser)
        {
            if (_layers.PrimaryLayerId is not { } primary)
            {
                SetStatus("橡皮擦需要一个主选图层。", isError: true);
                return;
            }
            targets = [primary];
        }
        else
        {
            targets = snapshot.Layers
                .Where(item => item.FloorId == floor.FloorId
                    && !item.IsDeleted && item.IsVisible && !item.IsLocked)
                .Select(item => item.LayerId)
                .ToArray();
        }
        SurveyLayerOperationResult? result;
        try
        {
            result = await _session.ApplyMaskStrokeAsync(
                floor.FloorId,
                targets,
                e.Points,
                _brushSize,
                _brushShape,
                _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }
        if (result is null)
        {
            _canvas.ClearMaskPreview();
            return;
        }
        var affected = result.Items.Count(item => item.Succeeded);
        SetStatus(affected == 0
            ? "笔划没有与可编辑图层相交。"
            : $"{(_eraseMode == SurveyEraseMode.Eraser ? "橡皮擦" : "砂纸")}已隐藏 {affected} 个图层中的指定区域。");
    }

    private void Canvas_ZoomChanged(object? sender, SurveyZoomChangedEventArgs e)
    {
        if (_zoomPercent is null)
            return;
        _updatingZoom = true;
        _zoomPercent.Value = Math.Round(e.Percent);
        _updatingZoom = false;
    }

    private async Task ShowProjectPropertiesAsync()
    {
        if (_session.Snapshot is not { } snapshot)
            return;
        var name = new TextBox { Header = "项目名称", Text = snapshot.Project.Name };
        var mapClass = new TextBox { Header = "Class", Text = snapshot.Project.MapClass };
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase));
        var floorName = new TextBox
        {
            Header = "当前楼层名称",
            Text = floor?.DisplayName ?? _floorKey
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(name);
        content.Children.Add(mapClass);
        content.Children.Add(floorName);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "测绘项目属性",
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        await _session.UpdateMetadataAsync(
            name.Text,
            mapClass.Text,
            floor?.FloorId,
            floorName.Text);
    }

    private async Task ExportCurrentFloorPngAsync()
    {
        if (_session.Snapshot is not { } snapshot)
            return;
        try
        {
            var picker = new FileSavePicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = CreatePngSuggestedFileName(snapshot.Project.Name, _floorKey),
                DefaultFileExtension = ".png",
                CommitButtonText = "导出",
                FileTypeChoices = { { "PNG 图片", new List<string> { ".png" } } }
            };
            var file = await picker.PickSaveFileAsync();
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
                return;
            SetStatus("正在导出当前楼层 PNG…");
            var result = await _session.RenderOutputsAsync(_floorKey);
            if (!result.Succeeded || result.Value is null)
            {
                SetStatus(result.Message ?? "PNG 导出失败。", isError: true);
                return;
            }
            await using var source = await _session.OpenAssetAsync(result.Value.VisualMap.Asset);
            await using var destination = new FileStream(
                file.Path, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination);
            SetStatus($"PNG 已导出：{file.Path}");
        }
        catch (Exception exception)
        {
            SetStatus($"PNG 导出失败：{exception.Message}", isError: true);
        }
    }

    private async Task ImportImageAsync()
    {
        if (_session.Snapshot is not { } snapshot)
            return;
        try
        {
            var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "选择",
                ViewMode = PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");
            var picked = await picker.PickSingleFileAsync();
            if (picked is null || string.IsNullOrWhiteSpace(picked.Path))
                return;
            var file = await StorageFile.GetFileFromPathAsync(picked.Path);

            byte[] bytes;
            int width, height;
            using (var stream = await file.OpenAsync(FileAccessMode.Read))
            {
                // 服务端不解码图片，这里用 BitmapDecoder 读取真实像素尺寸。
                var decoder = await BitmapDecoder.CreateAsync(stream);
                width = (int)decoder.PixelWidth;
                height = (int)decoder.PixelHeight;
                stream.Seek(0);
                using var memory = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            if (bytes.Length == 0)
            {
                SetStatus("选中的图片为空。", isError: true);
                return;
            }
            if (width <= 0 || height <= 0)
            {
                SetStatus("无法识别该图片的尺寸。", isError: true);
                return;
            }

            var extension = file.FileType.ToLowerInvariant();
            var mediaType = extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".bmp" => "image/bmp",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };

            var floor = await PickImportFloorAsync(snapshot, _floorKey);
            if (floor is null)
                return;

            SetStatus("正在导入图片…");
            var result = await _session.ImportObservationAsync(
                bytes,
                extension,
                mediaType,
                width,
                height,
                floor.FloorKey,
                Path.GetFileName(file.Path),
                _lifetimeCancellation.Token);
            if (result is null)
                return;
            SetStatus(result.WasAlreadyCommitted
                ? "该图片已经导入到当前楼层，未创建重复图层。"
                : $"已导入“{result.Layer.Name}”。");

            // 导入到当前楼层时画布由图层面板刷新自动更新；跨楼层需手动切换。
            if (!string.Equals(floor.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase))
            {
                _floorKey = floor.FloorKey;
                _floorPicker.SelectedItem = _floorPicker.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item =>
                        string.Equals(item.Tag as string, _floorKey, StringComparison.OrdinalIgnoreCase));
                _layers.SetFloor(_floorKey);
                await RenderCanvasAsync(_floorKey, fitAfterRender: true);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"图片导入失败：{exception.Message}", isError: true);
        }
    }

    private async Task<SurveyFloor?> PickImportFloorAsync(
        SurveyProjectSnapshot snapshot,
        string currentFloorKey)
    {
        var combo = new ComboBox
        {
            Header = "目标楼层",
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = snapshot.Floors.OrderBy(floor => floor.Order)
                .Select(floor => new ComboBoxItem
                {
                    Content = $"{floor.DisplayName}（{floor.FloorKey}）",
                    Tag = floor.FloorKey
                })
                .ToList()
        };
        combo.SelectedItem = combo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                string.Equals(item.Tag as string, currentFloorKey, StringComparison.OrdinalIgnoreCase))
            ?? combo.Items.OfType<ComboBoxItem>().FirstOrDefault();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入本地图片",
            Content = new StackPanel { Spacing = 8, Children = { combo } },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return combo.SelectedItem is ComboBoxItem { Tag: string floorKey }
            ? snapshot.Floors.FirstOrDefault(floor =>
                string.Equals(floor.FloorKey, floorKey, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private static string CreatePngSuggestedFileName(string projectName, string floorKey)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var raw = $"{projectName}-{floorKey}";
        var sanitized = new string(raw
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .Trim()
            .TrimEnd('.', ' ');
        while (sanitized.Contains("__", StringComparison.Ordinal))
            sanitized = sanitized.Replace("__", "_", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "Identity-Vision-Bridge-Map";
        if (IsReservedWindowsFileName(sanitized))
            sanitized = $"IDVB-{sanitized}";
        return sanitized.Length <= 120 ? sanitized : sanitized[..120].TrimEnd('.', ' ');
    }

    private static bool IsReservedWindowsFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4
                && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && stem[3] is >= '1' and <= '9');
    }

    private void SetStatus(string message, bool isError = false)
    {
        if (_disposed)
            return;
        _status.Text = message;
        _status.Foreground = new SolidColorBrush(isError
            ? Color.FromArgb(255, 255, 125, 104)
            : Color.FromArgb(255, 151, 166, 187));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _lifetimeCancellation.Cancel();
        _renderCancellation?.Cancel();
        _renderCancellation = null;
        _transformCommitDelay?.Cancel();
        _transformCommitDelay?.Dispose();
        _transformCommitDelay = null;
        _session.SnapshotChanged -= Session_SnapshotChanged;
        _session.Error -= Session_Error;
        _layers.SelectionChanged -= Layers_SelectionChanged;
        _layers.IsolationChanged -= Layers_IsolationChanged;
        _canvas.LayerSelected -= Canvas_LayerSelected;
        _canvas.LayerToolInvoked -= Canvas_LayerToolInvoked;
        _canvas.LayerPixelSampleRequested -= Canvas_LayerPixelSampleRequested;
        _canvas.MaskStrokeCommitted -= Canvas_MaskStrokeCommitted;
        _canvas.TransformCommitted -= Canvas_TransformCommitted;
        _canvas.ZoomChanged -= Canvas_ZoomChanged;
        DisposeKeyboardNavigation();
        _floorPicker.SelectionChanged -= FloorPicker_SelectionChanged;
        _layers.Dispose();
        _canvas.Dispose();
        _session.Dispose();
        foreach (var button in _toolButtons.Values)
            button.IsEnabled = false;
        _toolButtons.Clear();
        CloseRequested = null;
        Content = null;
        _lifetimeCancellation.Dispose();
    }
}
