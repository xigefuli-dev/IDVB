namespace IDVBuff.Modules;

/// <summary>
/// Single composition root for shell navigation. Add imported project modules here.
/// </summary>
public static class ModuleRegistration
{
    public static ModuleCatalog CreateCatalog()
    {
        var catalog = new ModuleCatalog();
        catalog.Register(new WelcomeModule());
        catalog.Register(new SettingsModule());
        catalog.Register(new StatusModule());
        catalog.Register(new ListModule());
        catalog.Register(new PluginsModule());

        // Example after adding a ProjectReference to an imported WinUI project:
        // catalog.Register(new ImportedProjectModule());
        return catalog;
    }

    public static IReadOnlyList<NavigationNode> CreateNavigation() =>
    [
        new NavigationNode("首页", Microsoft.UI.Xaml.Controls.Symbol.Home, "home"),
        new NavigationNode(
            "加页手记",
            Microsoft.UI.Xaml.Controls.Symbol.Document,
            children:
            [
                new NavigationNode("配置", Microsoft.UI.Xaml.Controls.Symbol.View, "map-status"),
                new NavigationNode("地图列表", Microsoft.UI.Xaml.Controls.Symbol.Bullets, "map-list")
            ],
            isExpanded: false),
        new NavigationNode(
            "插件",
            Microsoft.UI.Xaml.Controls.Symbol.AllApps,
            "plugins",
            iconGlyph: "\uEA86")
    ];
}
