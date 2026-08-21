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
        var orderedFloors = MapFloorRules.GetOrderedFloors(map);
        var firstFloorKey = orderedFloors.FirstOrDefault()?.Key ?? map.Recognition.FirstFloor.FloorKey;
        var secondFloorKey = orderedFloors.Skip(1).FirstOrDefault()?.Key ?? map.Recognition.SecondFloor.FloorKey;
        previews.Children.Add(CreatePreviewLayer(GetMapPreviewPath(map, secondFloorKey), new Thickness(16, 0, 0, 14)));
        previews.Children.Add(CreatePreviewLayer(GetMapPreviewPath(map, firstFloorKey), new Thickness(0, 14, 16, 0)));
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
        return card;
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
