using IDVBuff.Features.Maps;
using IDVBuff.Features.Accounts;
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

public sealed partial class MapListPage : UserControl
{
    private Border CreateMapCard(MapRecord map)
    {
        var card = new Border
        {
            Margin = new Thickness(11),
            Padding = new Thickness(11),
            Background = FluentTheme.CardBrush(),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(9)
        };
        _cardBorders[map.Id] = card;
        AttachCardInteractionFeedback(card);
        AttachHoldPreview(card, map);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previews = new Grid { Margin = new Thickness(11, 0, 11, 0), Height = 160 };
        previews.SizeChanged += (_, _) =>
        {
            if (previews.ActualWidth > 0)
                previews.Height = Math.Round(previews.ActualWidth / 1.6);
        };
        var scanFloorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        previews.Children.Add(CreatePreviewLayer(
            GetMapPreviewPath(map, scanFloorKey),
            new Thickness(0)));
        previews.Children.Add(CreateMapOriginBadge(map));
        content.Children.Add(previews);

        var label = new TextBlock
        {
            Text = map.DisplayName,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0)
        };
        Grid.SetRow(label, 1);
        content.Children.Add(label);
        var readiness = new TextBlock
        {
            Text = BuildRecognitionSummary(map),
            FontSize = 11,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(readiness, 2);
        content.Children.Add(readiness);
        card.Child = content;

        card.Tapped += (_, _) => SelectMap(map);
        card.DoubleTapped += async (_, _) =>
        {
            SelectMap(map);
            await ImportMapAsync(map);
        };
        if (App.IsSafeMode)
        {
            var flyout = new MenuFlyout();
            var showNow = new MenuFlyoutItem { Text = "立刻展示" };
            showNow.Click += async (_, _) => await ShowMapImmediatelyAsync(map);
            flyout.Items.Add(showNow);
            card.ContextFlyout = flyout;
        }
        return card;
    }

    private static Border CreateMapOriginBadge(MapRecord map)
    {
        var label = map.AcquisitionKind switch
        {
            MapAcquisitionKind.ImportedPackage => "导入",
            MapAcquisitionKind.Subscription => "订阅",
            _ when string.Equals(map.Source, "survey", StringComparison.Ordinal) => "本地 · 测绘",
            _ => "本地"
        };
        var name = map.SubscriptionPublisherHandle;
        if (name?.StartsWith("@u_", StringComparison.OrdinalIgnoreCase) == true
            && string.Equals(name, AccountSession.Identity?.PublisherHandle, StringComparison.OrdinalIgnoreCase))
            name = AccountSession.Identity!.DisplayName;
        else if (string.IsNullOrWhiteSpace(name) || name.StartsWith("@u_", StringComparison.OrdinalIgnoreCase))
            name = "未知作者";
        if (map.AcquisitionKind == MapAcquisitionKind.Subscription)
            label += " · @" + name.TrimStart('@');
        var background = map.SubscriptionPublisherIsBuilder
            ? Color.FromArgb(225, 233, 133, 34)
            : map.SubscriptionPublisherIsOfficial
                ? Color.FromArgb(225, 22, 135, 255)
                : Color.FromArgb(205, 22, 29, 38);
        var badge = new Border
        {
            Background = new SolidColorBrush(background),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                FontSize = 11
            }
        };
        if (map.AcquisitionKind == MapAcquisitionKind.Subscription)
            ToolTipService.SetToolTip(
                badge,
                $"订阅版本：{map.SubscriptionVersion ?? "未知"}");
        return badge;
    }


    private async Task ShowMapImmediatelyAsync(MapRecord map)
    {
        var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
        if (MapFloorRules.GetFloorProfile(map, floorKey) is null)
            return;
        var path = _repository.GetFloorOverlayPath(map, floorKey);
        if (!File.Exists(path))
            path = _repository.GetFloorRecognitionPath(map, floorKey);
        if (!File.Exists(path))
        {
            await ShowMessageAsync("无法展示地图", "没有找到该地图的楼层图片。");
            return;
        }
        await DirectMapDisplayWindow.ShowAsync(map, _repository, floorKey);
    }

    private Border CreatePreviewLayer(string path, Thickness margin)
    {
        var border = new Border
        {
            Margin = margin,
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 196, 196)),
            CornerRadius = new CornerRadius(6)
        };
        if (File.Exists(path))
        {
            var image = new Image
            {
                Stretch = Stretch.UniformToFill
            };
            image.Loaded += (_, _) => image.Source ??= GetPreviewBitmap(path);
            border.Child = image;
        }
        return border;
    }

    private string GetMapPreviewPath(MapRecord map, string floorKey)
    {
        if (MapFloorRules.GetOrderedFloors(map).All(floor => !string.Equals(floor.Key, floorKey, StringComparison.Ordinal)))
            return string.Empty;
        var thumbnailPath = _repository.GetFloorThumbnailPath(map, floorKey);
        return File.Exists(thumbnailPath)
            ? thumbnailPath
            : _repository.GetFloorRecognitionPath(map, floorKey);
    }

    private BitmapImage GetPreviewBitmap(string path)
    {
        if (_previewImages.TryGetValue(path, out var bitmap))
            return bitmap;
        bitmap = CreateBitmap(path, decodePixelWidth: 400);
        _previewImages[path] = bitmap;
        return bitmap;
    }

    private void SelectMap(MapRecord map)
    {
        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        var isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (isCtrl)
        {
            // Ctrl+Click: toggle selection
            if (_selectedMapIds.Contains(map.Id))
                _selectedMapIds.Remove(map.Id);
            else
                _selectedMapIds.Add(map.Id);
            _lastClickedMapId = map.Id;
        }
        else if (isShift && _lastClickedMapId is { } lastId)
        {
            // Shift+Click: range select from last clicked to this item
            var orderedIds = GetVisibleMaps().Select(m => m.Id).ToList();
            var lastIndex = orderedIds.IndexOf(lastId);
            var currentIndex = orderedIds.IndexOf(map.Id);
            if (lastIndex >= 0 && currentIndex >= 0)
            {
                var start = Math.Min(lastIndex, currentIndex);
                var end = Math.Max(lastIndex, currentIndex);
                for (var i = start; i <= end; i++)
                    _selectedMapIds.Add(orderedIds[i]);
            }
            // Don't update _lastClickedMapId on shift-click to allow extending range
        }
        else
        {
            // Plain click: single select
            _selectedMapIds = [map.Id];
            _lastClickedMapId = map.Id;
        }

        UpdateSelectedCardVisuals();
    }

    private void UpdateSelectedCardVisuals()
    {
        foreach (var (id, card) in _cardBorders)
        {
            var selected = _selectedMapIds.Contains(id);
            var group = _variantGroups.FirstOrDefault(candidate => candidate.MapIds.Contains(id));
            if (selected)
            {
                card.Background = FluentTheme.Brush("AccentFillColorSecondaryBrush");
                card.BorderBrush = new SolidColorBrush(AccentBlue);
            }
            else if (group is not null && group.PaletteSlot is >= 0 and < 12)
            {
                var palette = VariantPalette[group.PaletteSlot];
                var dark = ActualTheme == ElementTheme.Dark;
                card.Background = new SolidColorBrush(dark ? palette.DarkFill : palette.LightFill);
                card.BorderBrush = new SolidColorBrush(dark ? palette.DarkOutline : palette.LightOutline);
            }
            else
            {
                card.Background = FluentTheme.CardBrush();
                card.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            }
        }

        if (_editButton is not null)
            _editButton.IsEnabled = HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = HasSelection;
        if (_variantButton is not null)
            _variantButton.IsEnabled = _selectedMapIds.Count >= 2;
    }

    private async Task ToggleSelectedVariantGroupAsync()
    {
        if (_selectedMapIds.Count < 2)
            return;
        try
        {
            await _repository.ToggleVariantGroupAsync(_selectedClass, _selectedMapIds);
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法绑定或解绑变体", exception.Message);
        }
    }

    private async Task EditMapAsync(MapRecord map)
    {
        try
        {
            var draft = await _repository.CreateDraftAsync(map.Id);
            if (draft is null)
            {
                await ShowMessageAsync("地图不存在", "该地图已被删除，请刷新列表。");
                await ShowListAsync();
                return;
            }
            _activeFloorKey = "1f";
            _activeAnchorId = null;
            if (!IsBatchOperation)
                ResetBatchImport();
            _draft = draft;
            _draft.Recognition.EnsureStandardAnchors();
            ShowMarkerEditor();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法编辑地图", exception.Message);
        }
    }

    private async Task DeleteSelectedMapAsync(MapRecord map)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除地图？",
            Content = $"将永久删除 {map.DisplayName} 及其两张图片和识别标记数据。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            await _repository.DeleteAsync(map.Id);
            if (!App.IsSafeMode)
                await App.Session.RefreshMapCacheAsync(map.Id);
            _selectedMapIds.Remove(map.Id);
            if (_lastClickedMapId == map.Id)
                _lastClickedMapId = null;
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("删除失败", exception.Message);
        }
    }
}
