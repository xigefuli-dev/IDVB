using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;
public sealed partial class TagsAndTemplatesPage : UserControl
{

    private async Task AddTemplateAsync()
    {
        var nameBox = new TextBox { Header = "模板名称", PlaceholderText = "例如：三层地图" };
        var floorsBox = new TextBox
        {
            Header = "楼层（每行填写 ID / 名称）", PlaceholderText = "1f / 一楼\n2f / 二楼",
            AcceptsReturn = true, Height = 130, TextWrapping = TextWrapping.Wrap
        };
        var panel = new StackPanel { Spacing = 14, Children = { nameBox, floorsBox } };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "新建模板", Content = panel,
            PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = nameBox.Text.Trim();
        var floors = floorsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('/', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 0 && parts[0].Length > 0 && parts[0].All(char.IsAsciiLetterOrDigit))
            .Select(parts => new MapFloorTemplate(parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0]))
            .GroupBy(floor => floor.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
        if (name.Length == 0 || floors.Length == 0) return;
        _customTemplates.Add(new MapTemplate($"custom-{Guid.NewGuid():N}", name, floors));
        await _templateStore.SaveAsync(_customTemplates);
        RenderTemplates();
    }
}
