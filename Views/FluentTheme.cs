using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

internal static class FluentTheme
{
    public static Brush Brush(string resourceKey) =>
        Application.Current.Resources[resourceKey] as Brush
        ?? throw new InvalidOperationException($"Missing WinUI theme resource '{resourceKey}'.");
}
