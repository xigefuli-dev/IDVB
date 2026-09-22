using Microsoft.UI.Xaml;

namespace IDVBuff.Lifecycle;

internal static class AppThemePreference
{
    public static ElementTheme Resolve(MainProgramPreferences preferences) =>
        preferences.GetAppearance().Mode switch
        {
            IDVBuff.Appearance.AppearanceMode.Dark => ElementTheme.Dark,
            IDVBuff.Appearance.AppearanceMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default
        };
}
