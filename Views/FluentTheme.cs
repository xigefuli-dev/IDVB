using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

// Compatibility names only. All colors and update ownership live in the theme service.
internal static class FluentTheme
{
    public static Brush Brush(string resourceKey) =>
        ThemeService.ApplicationResources[ThemeResourceKeys.Resolve(resourceKey)];
    public static Brush CardBrush() => ThemeService.ApplicationResources[ThemeToken.Card];
    public static Brush WindowBrush() => ThemeService.ApplicationResources[ThemeToken.Window];
    public static Brush Brush(FrameworkElement owner, string resourceKey) =>
        Brush(owner, ThemeResourceKeys.Resolve(resourceKey));

    public static Brush Brush(FrameworkElement owner, ThemeToken token) => ThemeService.For(owner)[token];
    public static Brush CardBrush(FrameworkElement owner) => Brush(owner, ThemeToken.Card);
    public static ThemeSnapshot Snapshot(FrameworkElement owner) => ThemeService.For(owner).Snapshot;
    public static void Observe(FrameworkElement owner, Action<ThemeSnapshot> changed) =>
        ThemeService.For(owner).Changed += changed;
}
