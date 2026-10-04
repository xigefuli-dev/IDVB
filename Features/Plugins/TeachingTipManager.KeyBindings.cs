using IDVBuff.PluginContracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Features.Plugins;

public sealed partial class TeachingTipManager
{
    private static void RefreshPluginBindingButtonAppearance(
        Button button,
        PluginInputBinding binding,
        bool recording,
        bool hovered)
    {
        var isConfigured = binding.IsConfigured;
        var showReset = !recording && isConfigured && hovered;
        button.Content = recording
            ? "请按按键…"
            : showReset ? "重置按键" : "设置按键";
        ThemeButton.Apply(button, showReset ? IDVBuff.Appearance.ThemeButtonRole.Danger
            : recording || !isConfigured ? IDVBuff.Appearance.ThemeButtonRole.Accent
            : IDVBuff.Appearance.ThemeButtonRole.Standard);
    }


}
