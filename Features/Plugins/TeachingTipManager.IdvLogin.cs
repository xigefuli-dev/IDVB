using System.Text.Json;
using IDVBuff.PluginContracts;
using IDVBuff.Plugins.IdvLogin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace IDVBuff.Features.Plugins;

public sealed partial class TeachingTipManager
{
    private FrameworkElement BuildLoginFolderPicker(IPluginSettingsProvider provider, string pluginId, PluginTextSetting setting)
    {
        var path = new TextBlock
        {
            Text = ReadProviderValue(provider, setting) as string ?? "",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 320
        };
        var choose = new Button { Content = "选择安装文件夹…" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 320 };
        choose.Click += async (_, _) =>
        {
            choose.IsEnabled = false;
            try
            {
                var picker = new FolderPicker(((App)Application.Current).MainWindow.AppWindow.Id)
                {
                    CommitButtonText = "选择 idv-login 文件夹"
                };
                var folder = await picker.PickSingleFolderAsync();
                if (folder is null) return;
                if (IdvLoginPlugin.ResolveStartInfo(folder.Path) is null)
                {
                    status.Text = "此文件夹中未找到受支持的 idv-login 安装";
                    return;
                }
                PersistSetting(provider, pluginId, setting.Key, JsonSerializer.SerializeToElement(folder.Path));
                path.Text = folder.Path;
                status.Text = "已保存，请启用插件";
            }
            catch { status.Text = "无法选择或保存文件夹，请重试"; }
            finally { choose.IsEnabled = true; }
        };
        return new StackPanel { Spacing = 6, Children = { path, choose, status } };
    }
}
