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
    private void UpdateMarkerConfirmState()
    {
        if (_markerConfirmButton is null || _draft is null)
            return;
        var canConfirm = _draft.Recognition.HasFirstFloorGateMarkers();
        _markerConfirmButton.IsEnabled = canConfirm;
        _markerConfirmButton.Background = new SolidColorBrush(
            canConfirm ? AccentBlue : _modernEditorActive ? EditorPanelRaised : DisabledGray);
        if (_modernEditorActive)
            _markerConfirmButton.Foreground = new SolidColorBrush(canConfirm ? EditorText : EditorMuted);
    }

    private async Task SaveDraftAsync()
    {
        if (_draft is null)
            return;
        try
        {
            var savedMap = await _repository.SaveAsync(_draft);
            _selectedMapIds.Add(savedMap.Id);

            // Old batch import flow (multiple image pairs dropped at once)
            if (TryAdvanceBatch())
            {
                ShowMarkerEditor();
                return;
            }

            // New multi-select batch operation flow
            if (TryAdvanceBatchQueue() && _batchQueue is not null)
            {
                var nextMap = _batchQueue[_batchQueueIndex];
                if (_batchType == BatchOperationType.Edit)
                    await EditMapAsync(nextMap);
                else if (_batchType == BatchOperationType.Import)
                    await ImportMapAsync(nextMap);
                return;
            }

            if (!App.IsSafeMode)
                await App.Session.RefreshMapCacheAsync(savedMap.Id);
            _draft = null;
            _activeAnchorId = null;
            ResetBatchImport();
            ResetBatchOperation();
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("保存失败", exception.Message);
        }
    }

    private TeachingTip CreateImportTeachingTip(Button importButton, Button exportButton)
    {
        var createMap = new Button
        {
            Content = "创建地图",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 150
        };
        var importPackage = new Button
        {
            Content = "导入数据包",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MinWidth = 150
        };
        var choices = new StackPanel { Spacing = 8 };
        choices.Children.Add(createMap);
        choices.Children.Add(importPackage);
        var tip = new TeachingTip
        {
            Target = importButton,
            Title = "请选择导入方式",
            Subtitle = "创建新地图，或导入其他用户分享的 IDVM 数据包。",
            Content = choices,
            IsLightDismissEnabled = true,
            PreferredPlacement = TeachingTipPlacementMode.Bottom
        };
        createMap.Click += async (_, _) =>
        {
            tip.IsOpen = false;
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            await ShowImportAsync(new MapDraft { Class = _selectedClass });
        };
        importPackage.Click += async (_, _) =>
        {
            tip.IsOpen = false;
            await ImportIdvmPackageAsync(importButton, exportButton);
        };
        return tip;
    }

    private async Task ImportIdvmPackageAsync(Button importButton, Button exportButton)
    {
        var packagePath = await PickIdvmPackageAsync();
        if (packagePath is null)
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在导入…");
        IdvmImportPlan? plan = null;
        try
        {
            plan = await Task.Run(() => _idvmPackageService.InspectAsync(packagePath));
            var result = await _idvmPackageService.ImportAsync(plan);
            plan = null; // ImportAsync owns disposal after this point.
            try
            {
                if (!App.IsSafeMode)
                    await App.Session.RefreshMapCacheAsync();
            }
            catch (Exception exception)
            {
                System.Diagnostics.Debug.WriteLine($"Imported maps but runtime refresh failed: {exception}");
            }

            // ShowListAsync rebuilds the action row. Clear the operation state
            // before rebuilding so the newly-created import/export buttons are
            // initialized with the completed state rather than inheriting the
            // old buttons' busy state.
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
            _selectedClass = result.CreatedClasses[0];
            _selectedMapIds.Clear();
            _lastClickedMapId = null;
            await ShowListAsync();
            // The list has already refreshed to the imported Class. Avoid
            // opening a second ContentDialog while a picker/confirmation is
            // still closing; WinUI permits only one ContentDialog at a time.
            System.Diagnostics.Debug.WriteLine(
                $"数据包导入完成：已创建 {result.CreatedClasses.Count} 个 Class，导入 {result.ImportedMaps.Count} 张地图。"
                + string.Join("、", result.CreatedClasses));
        }
        catch (Exception exception)
        {
            if (plan is not null)
                await plan.DisposeAsync();
            await ShowMessageAsync("数据包导入失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private async Task ShowExportDialogAsync(Button importButton, Button exportButton)
    {
        var currentCount = GetVisibleMaps().Count;
        var totalCount = _loadedMaps.Count;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = $"当前 Class：{_selectedClass}（{currentCount} 张地图）",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = $"全部非空 Class：{_classes.Count(name => _loadedMaps.Any(map => string.Equals(map.Class, name, StringComparison.OrdinalIgnoreCase)))} 个，{totalCount} 张地图",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出 IDVM 数据包",
            Content = content,
            PrimaryButtonText = "当前 Class",
            SecondaryButtonText = "全部地图",
            CloseButtonText = "取消",
            IsPrimaryButtonEnabled = currentCount > 0,
            IsSecondaryButtonEnabled = totalCount > 0,
            DefaultButton = currentCount > 0
                ? ContentDialogButton.Primary
                : ContentDialogButton.Secondary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None)
            return;
        var scope = result == ContentDialogResult.Primary
            ? IdvmExportScope.CurrentClass
            : IdvmExportScope.AllClasses;
        var suggestedName = scope == IdvmExportScope.CurrentClass
            ? $"IDVB-{SanitizeFileName(_selectedClass)}-{DateTime.Now:yyyyMMdd-HHmmss}"
            : $"IDVB-All-{DateTime.Now:yyyyMMdd-HHmmss}";
        var destination = await PickIdvmDestinationAsync(suggestedName);
        if (destination is null)
            return;

        SetPackageOperationState(importButton, exportButton, isBusy: true, "正在导出…");
        try
        {
            await Task.Run(() => _idvmPackageService.ExportAsync(
                scope,
                scope == IdvmExportScope.CurrentClass ? _selectedClass : null,
                destination));
            await ShowMessageAsync("数据包导出完成", $"已保存到：\n{destination}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("数据包导出失败", exception.Message);
        }
        finally
        {
            SetPackageOperationState(importButton, exportButton, isBusy: false, null);
        }
    }

    private void SetPackageOperationState(
        Button importButton,
        Button exportButton,
        bool isBusy,
        string? busyText)
    {
        _isPackageOperation = isBusy;
        importButton.IsEnabled = !isBusy;
        exportButton.IsEnabled = !isBusy && _loadedMaps.Count > 0;
        exportButton.Content = isBusy ? busyText : "导出";
    }

    private async Task<string?> PickIdvmPackageAsync()
    {
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "导入",
                ViewMode = PickerViewMode.List
            };
            picker.FileTypeFilter.Add(".idvm");
            var result = await picker.PickSingleFileAsync();
            return result is null || string.IsNullOrWhiteSpace(result.Path)
                ? null
                : result.Path;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件选择器", exception.Message);
            return null;
        }
    }

    private async Task<string?> PickIdvmDestinationAsync(string suggestedName)
    {
        try
        {
            var picker = new FileSavePicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
                DefaultFileExtension = ".idvm",
                CommitButtonText = "导出",
                FileTypeChoices =
                {
                    { "IDVM 地图数据包", new List<string> { ".idvm" } }
                }
            };
            var result = await picker.PickSaveFileAsync();
            if (result is null || string.IsNullOrWhiteSpace(result.Path))
                return null;
            return System.IO.Path.GetExtension(result.Path).Equals(".idvm", StringComparison.OrdinalIgnoreCase)
                ? result.Path
                : result.Path + ".idvm";
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开保存选择器", exception.Message);
            return null;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "Class" : sanitized;
    }

    private async Task<string?> PickImageAsync(string title)
    {
        PickFileResult? result;
        try
        {
            var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "选择",
                ViewMode = PickerViewMode.Thumbnail
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            result = await picker.PickSingleFileAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件选择器", $"{title}失败：{exception.Message}");
            return null;
        }

        if (result is null)
            return null;

        try
        {
            if (string.IsNullOrWhiteSpace(result.Path) || !File.Exists(result.Path))
                throw new FileNotFoundException("选择的图片不存在。", result.Path);

            var file = await StorageFile.GetFileFromPathAsync(result.Path);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            _ = await BitmapDecoder.CreateAsync(stream);
            return result.Path;
        }
        catch
        {
            await ShowMessageAsync("无法读取图片", "请选择有效的 PNG、JPG 或 JPEG 图片。");
            return null;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }

    private FloorRecognitionProfile GetActiveFloorProfile()
    {
        if (_draft is null)
            throw new InvalidOperationException("当前没有可编辑的地图。");
        return _draft.Recognition.GetFloor(_activeFloorKey)
            ?? throw new InvalidOperationException($"不存在的楼层 '{_activeFloorKey}'。");
    }

    private RecognitionAnchor? GetActiveAnchor() =>
        _activeAnchorId is { } id ? GetActiveFloorProfile().FindAnchor(id) : null;

    private string? GetActiveFloorImagePath() => _draft is null
        ? null
        : _draft.FloorPaths.TryGetValue(_activeFloorKey, out var path)
            ? path
            : _activeFloorKey == "1f" ? _draft.FloorOnePath : _draft.FloorTwoPath;

    private static void ClearFloorAnchors(MapDraft draft, string floorKey)
    {
        draft.Recognition.EnsureStandardAnchors();
        var profile = draft.Recognition.GetFloor(floorKey)
            ?? throw new InvalidOperationException($"不存在的楼层 '{floorKey}'。");
        profile.RecognitionRegion = null;
        foreach (var anchor in profile.Anchors)
            anchor.Bounds = null;
    }

    private static bool HasAnyFloorImage(MapDraft draft) =>
        draft.FloorPaths.Count > 0
        && draft.FloorPaths.Values.Any(path => MapRepository.IsSupportedImage(path) && File.Exists(path));

    private static string BuildRecognitionSummary(MapRecord map) =>
        $"一楼：{BuildFloorSummary(map.Recognition.FirstFloor)} · 二楼：{BuildFloorSummary(map.Recognition.SecondFloor)}";

    private static string BuildFloorSummary(FloorRecognitionProfile floor)
    {
        var required = floor.RequiredAnchors.ToArray();
        var markedRequired = required.Count(anchor => anchor.IsMarked);
        var markedOptional = floor.Anchors.Count(anchor =>
            anchor.Role == RecognitionAnchorRole.Optional && anchor.IsMarked);
        return $"{markedRequired}/{required.Length} 必需，{markedOptional} 辅助";
    }

    private static Color GetAnchorColor(RecognitionAnchor anchor) => anchor.Key switch
    {
        "main-entrance" => MainEntranceBlue,
        "side-entrance" => SideEntranceGreen,
        "second-floor-primary" => SecondFloorPurple,
        _ => OptionalAnchorOrange
    };

    private static NormalizedRectangle CreateNormalizedRectangle(Point start, Point end) => new()
    {
        X = Math.Min(start.X, end.X),
        Y = Math.Min(start.Y, end.Y),
        Width = Math.Abs(end.X - start.X),
        Height = Math.Abs(end.Y - start.Y)
    };

    private static NormalizedRectangle ToSourceRectangle(
        NormalizedRectangle regionRelative,
        NormalizedRectangle region) => new()
    {
        X = region.X + regionRelative.X * region.Width,
        Y = region.Y + regionRelative.Y * region.Height,
        Width = regionRelative.Width * region.Width,
        Height = regionRelative.Height * region.Height
    };

    /// <summary>将 root 的最大高度约束到父 ScrollViewer 的视口高度。</summary>
    private void ApplyViewportConstraint(FrameworkElement root)
    {
        if (ParentScrollViewer is { } scroller && scroller.ActualHeight > 0)
        {
            root.MaxHeight = scroller.ActualHeight;
            root.MinHeight = 0;
        }
        else
        {
            root.MinHeight = 630;
        }

        if (ParentScrollViewer is not null)
        {
            ParentScrollViewer.SizeChanged -= OnParentScrollViewerSizeChanged;
            ParentScrollViewer.SizeChanged += OnParentScrollViewerSizeChanged;
        }
    }

    private void OnParentScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_workflowHost.Content is FrameworkElement root)
            root.MaxHeight = e.NewSize.Height;
    }

    private static TextBlock CreateTitle(string text) => new()
    {
        Text = text,
        FontSize = 29,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Left
    };

    private static TextBlock CreateDescription(string text) => new()
    {
        Text = text,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 828,
        HorizontalAlignment = HorizontalAlignment.Left,
        TextAlignment = TextAlignment.Left
    };

    private static Button CreateActionButton(string text, Color color)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(color),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            FontSize = 14,
            MinWidth = 108,
            MinHeight = 45,
            Padding = new Thickness(20, 7, 20, 7),
            CornerRadius = new CornerRadius(8)
        };
        AttachHoverFeedback(button);
        return button;
    }
}
