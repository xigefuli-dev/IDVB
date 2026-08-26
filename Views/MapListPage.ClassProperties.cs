using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private async Task ShowClassPropertiesDialogAsync()
    {
        if (_isPackageOperation || string.IsNullOrWhiteSpace(_selectedClass))
            return;

        var current = _classProperties.TryGetValue(_selectedClass, out var value)
            ? value.Clone()
            : new MapClassProperties();
        var toggle = new ToggleSwitch
        {
            Header = "去除背景",
            IsOn = current.RemoveBackground,
            OffContent = "关闭",
            OnContent = "开启",
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush")
        };
        var content = new StackPanel { Spacing = 12, MinWidth = 320 };
        content.Children.Add(new TextBlock
        {
            Text = $"地图类：{_selectedClass}",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "该设置会重建此类的全部地图和楼层。人工遮瑕层始终保留。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush")
        });
        content.Children.Add(toggle);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "地图类编辑",
            Content = content,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || toggle.IsOn == current.RemoveBackground)
            return;

        SetClassEditBusy(true);
        try
        {
            await _repository.SetClassRemoveBackgroundAsync(_selectedClass, toggle.IsOn);
            _classProperties = (await _repository.GetCatalogSnapshotAsync()).ClassProperties;
            _selectedMapIds.Clear();
            if (!App.IsSafeMode)
                await App.Session.RefreshMapCacheAsync();
            await ShowListAsync();
        }
        catch (Exception exception)
        {
            var failure = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "地图类更新失败",
                Content = new TextBlock
                {
                    Text = exception.Message,
                    TextWrapping = TextWrapping.Wrap
                },
                CloseButtonText = "关闭"
            };
            await failure.ShowAsync();
        }
        finally
        {
            SetClassEditBusy(false);
        }
    }

    private void SetClassEditBusy(bool busy)
    {
        if (_classEditButton is not null)
        {
            _classEditButton.IsEnabled = !busy;
            _classEditButton.Content = busy ? "处理中…" : "地图类编辑";
        }
        if (_classComboBox is not null)
            _classComboBox.IsEnabled = !busy;
        if (_editButton is not null)
            _editButton.IsEnabled = !busy && HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = !busy && HasSelection;
        if (_importButton is not null)
            _importButton.IsEnabled = !busy && !_isPackageOperation;
        if (_exportButton is not null)
            _exportButton.IsEnabled = !busy && !_isPackageOperation && _loadedMaps.Count > 0;
    }
}
