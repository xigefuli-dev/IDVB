using IDVBuff.Appearance;
using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private async Task ExportClassOverviewAsync()
    {
        if (_isPackageOperation || string.IsNullOrWhiteSpace(_selectedClass))
            return;
        var selectedClass = _selectedClass;
        var snapshot = await _repository.GetCatalogSnapshotAsync();
        var maps = snapshot.Maps.Where(map => string.Equals(
            map.Class, selectedClass, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (maps.Length == 0)
        {
            await ShowMessageAsync("无法导出全览图", "当前地图类没有地图。");
            return;
        }

        string? directory;
        try
        {
            var picker = new FolderPicker(((App)Application.Current).MainWindow.AppWindow.Id)
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                CommitButtonText = "导出到此文件夹"
            };
            directory = (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("无法打开文件夹选择器", exception.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(directory))
            return;

        // Preserve group colors in the exported file even when the UI uses high contrast.
        var theme = FluentTheme.Snapshot(this) with { IsHighContrast = false };
        var colors = snapshot.VariantGroups
            .Where(group => string.Equals(group.Class, selectedClass, StringComparison.OrdinalIgnoreCase))
            .SelectMany(group => group.MapIds.Select(id => (Id: id, Colors: MapCardPalette.Resolve(
                theme, group.PaletteSlot, selected: false))))
            .ToDictionary(item => item.Id, item => item.Colors);
        SetClassEditBusy(true);
        try
        {
            var exported = await Task.Run(() => MapClassOverviewExporter.Export(
                directory,
                maps,
                _repository.GetFloorImagePath,
                map => colors.TryGetValue(map.Id, out var color) ? color : null));
            await ShowMessageAsync("全览图导出完成",
                $"已导出 {exported.Count} 张楼层全览图：\n{directory}");
        }
        catch (Exception exception)
        {
            await ShowMessageAsync("全览图导出失败", exception.Message);
        }
        finally
        {
            SetClassEditBusy(false);
        }
    }
}
