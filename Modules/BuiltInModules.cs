using IDVBuff.ModuleContracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Modules;

internal sealed class WelcomeModule : IAppModule
{
    public string Id => "home";
    public string DisplayName => "首页";
    public string IconKey => nameof(Symbol.Home);
    public object CreateView() => new IDVBuff.Views.HomePage();
}

internal sealed class FundamentalsModule : IAppModule
{
    public string Id => "fundamentals";
    public string DisplayName => "Fundamentals";
    public string IconKey => nameof(Symbol.AllApps);
    public object CreateView() => ModuleViewFactory.Create(
        "Fundamentals",
        "Use this area for the core Identity Vision Bridge tools. Each module keeps its own views, services, and dependencies.",
        "Built-in module");
}

internal sealed class SettingsModule : IAppModule
{
    public string Id => "settings";
    public string DisplayName => "Settings";
    public string IconKey => nameof(Symbol.Setting);
    public object CreateView() => new IDVBuff.Views.SettingsPage();
}

internal sealed class StatusModule : IAppModule
{
    public string Id => "map-status";
    public string DisplayName => "配置";
    public string IconKey => nameof(Symbol.View);
    public object CreateView() => new IDVBuff.Views.MapStatusPage();
}

internal sealed class ListModule : IAppModule
{
    public string Id => "map-list";
    public string DisplayName => "地图列表";
    public string IconKey => nameof(Symbol.Bullets);
    public object CreateView() => new IDVBuff.Views.MapListPage();
}

internal static class ModuleViewFactory
{
    public static FrameworkElement Create(string title, string description, string badge) => new StackPanel
    {
        Margin = new Thickness(48, 42, 48, 72),
        Spacing = 16,
        Children =
        {
            new TextBlock
            {
                Text = title,
                FontSize = 29,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextAlignment = TextAlignment.Left
            },
            new TextBlock
            {
                Text = description,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Left,
                TextAlignment = TextAlignment.Left
            },
            new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 226, 239, 255)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = badge, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 90, 158)) }
            }
        }
    };
}
