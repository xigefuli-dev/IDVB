using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class ScanModeSelector
{
    private ThemeSnapshot? _surfaceTheme;
    private readonly Border _sheen = new()
    {
        Width = 354,
        Height = 74,
        Margin = new Thickness(0, 7, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Top,
        CornerRadius = new CornerRadius(23),
        IsHitTestVisible = false
    };

    // The selector's in-app acrylic is independent of the window backdrop material.
    // Traditional solid windows also retain this component's glass and ambient light.
    internal static bool AllowsGlass(ThemeSnapshot theme) => !theme.IsHighContrast
        && theme.FallbackReason != "TransparencyDisabled";

    private void UpdateGlassSurface(ThemeSnapshot theme)
    {
        if (ReferenceEquals(_surfaceTheme, theme)) return;
        _surfaceTheme = theme;
        var white = Microsoft.UI.Colors.White;
        var black = Microsoft.UI.Colors.Black;
        var glass = AllowsGlass(theme);
        _card.Background = glass ? new AcrylicBrush
        {
            TintColor = ThemeResources.ToColor(theme[ThemeToken.Raised]),
            TintOpacity = theme.IsDark ? .46 : .50,
            TintLuminosityOpacity = theme.IsDark ? .16 : .22,
            FallbackColor = ThemeResources.ToColor(theme[ThemeToken.Card])
        } : FluentTheme.CardBrush(this);
        _card.BorderBrush = theme.IsHighContrast ? FluentTheme.Brush(this, ThemeToken.ControlBorder)
            : Gradient(new Point(0, 0), new Point(1, 1),
                (WithAlpha(white, theme.IsDark ? (byte)105 : (byte)235), 0),
                (WithAlpha(white, theme.IsDark ? (byte)34 : (byte)128), .48),
                (WithAlpha(black, 54), 1));
        _sheen.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        _sheen.Background = Gradient(new Point(0, 0), new Point(0, 1),
            (WithAlpha(white, theme.IsDark ? (byte)30 : (byte)60), 0),
            (WithAlpha(white, theme.IsDark ? (byte)8 : (byte)15), .45),
            (WithAlpha(white, 0), 1));
        _trackSurface.Background = theme.IsHighContrast ? FluentTheme.Brush(this, ThemeToken.ControlFill)
            : new SolidColorBrush(Color.FromArgb(62, 128, 128, 128));
        _trackSurface.BorderBrush = theme.IsHighContrast ? FluentTheme.Brush(this, ThemeToken.ControlBorder)
            : new SolidColorBrush(Color.FromArgb(42, 128, 128, 128));
    }

    private static LinearGradientBrush CreateSelectionBrush(Color color)
    {
        var end = SelectionStop(color, 1.03, 242);
        // Shade the readable endpoint so contrast correction cannot flatten the
        // gradient into two nearly identical colors (especially the green mode).
        return Gradient(new Point(0, .5), new Point(1, .5),
            (SelectionStop(end, .66, 226), 0), (end, 1));
    }

    private static Color SelectionStop(Color color, double shade, byte alpha)
    {
        var seed = new RgbColor(
            (byte)Math.Clamp(Math.Round(color.R * shade), 0, 255),
            (byte)Math.Clamp(Math.Round(color.G * shade), 0, 255),
            (byte)Math.Clamp(Math.Round(color.B * shade), 0, 255));
        var white = new RgbColor(255, 255, 255);
        // Preserve the original horizontal gradient and translucent stops. Check
        // white selected text against the brightest possible backdrop, not just
        // the opaque stop; otherwise light transmitted through glass can wash it out.
        for (var step = 0; step <= 100; step++)
        {
            var candidate = RgbColor.Mix(seed, new(0, 0, 0), step / 100d);
            var composite = RgbColor.Mix(white, candidate, alpha / 255d);
            // Leave headroom for byte rounding as both color and alpha interpolate.
            if (RgbColor.Contrast(white, composite) >= 4.6)
                return WithAlpha(ThemeResources.ToColor(candidate), alpha);
        }
        throw new InvalidOperationException("扫描模式渐变无法保证选中文字的对比度。");
    }

    private static LinearGradientBrush Gradient(Point start, Point end, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (color, offset) in stops)
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
        return brush;
    }
}
