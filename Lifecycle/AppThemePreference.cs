using Microsoft.UI.Xaml;

namespace IDVBuff.Lifecycle;

internal static class AppThemePreference
{
    public static ElementTheme Resolve(MainProgramPreferences preferences) =>
        preferences.FollowSystemTheme
            ? ElementTheme.Default
            : preferences.UseDarkTheme
                ? ElementTheme.Dark
                : ElementTheme.Light;
}
