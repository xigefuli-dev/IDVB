using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Views;

internal static class FluentTheme
{
    public static bool UseLegacyTheme { get; private set; }

    public static SystemBackdrop? CreateWindowBackdrop(bool useLegacyTheme)
    {
        UseLegacyTheme = useLegacyTheme;
        return useLegacyTheme ? null : new GaussianBlurBackdrop();
    }

    public static Brush Brush(string resourceKey) =>
        Application.Current.Resources[resourceKey] as Brush
        ?? throw new InvalidOperationException($"Missing WinUI theme resource '{resourceKey}'.");

    // These are deliberately kept here instead of scattered through the pages so the
    // acrylic balance can be tuned in one place. The alpha channel is the requested
    // fill opacity; the effective theme (root element's ActualTheme) selects the
    // corresponding light/dark color, matching the acrylic backdrop.
    // Tune these as ordinary percentages (0-100), independently per theme.
    private const double LightWindowFillPercent = 50;
    private const double DarkWindowFillPercent = 45;
    private const double LightCardFillPercent = 10;
    private const double DarkCardFillPercent = 10;

    private static readonly Color LightWindowColor = Color.FromArgb(Alpha(LightWindowFillPercent), 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkWindowColor = Color.FromArgb(Alpha(DarkWindowFillPercent), 0x00, 0x00, 0x00);
    private static readonly Color LightCardColor = Color.FromArgb(Alpha(LightCardFillPercent), 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkCardColor = Color.FromArgb(Alpha(DarkCardFillPercent), 0x2D, 0x2D, 0x2D);

    private static readonly List<WeakReference<SolidColorBrush>> CardBrushes = new();
    private static readonly List<WeakReference<SolidColorBrush>> WindowBrushes = new();
    private static readonly object Gate = new();
    private static FrameworkElement? _themeRoot;

    public static Brush CardBrush()
    {
        if (UseLegacyTheme)
            return Brush("CardBackgroundFillColorDefaultBrush");

        var brush = new SolidColorBrush(IsDarkTheme() ? DarkCardColor : LightCardColor);
        lock (Gate)
        {
            CardBrushes.RemoveAll(weak => !weak.TryGetTarget(out _));
            CardBrushes.Add(new WeakReference<SolidColorBrush>(brush));
        }
        return brush;
    }

    public static Brush WindowBrush()
    {
        if (UseLegacyTheme)
            return Brush("ApplicationPageBackgroundThemeBrush");

        var brush = new SolidColorBrush(IsDarkTheme() ? DarkWindowColor : LightWindowColor);
        lock (Gate)
        {
            WindowBrushes.RemoveAll(weak => !weak.TryGetTarget(out _));
            WindowBrushes.Add(new WeakReference<SolidColorBrush>(brush));
        }
        return brush;
    }

    /// <summary>
    /// 注册主题根元素（应用根页面）。卡片/窗口颜色跟随它的 ActualTheme。
    /// 背景模糊层（GaussianBlurBackdrop）不染色，窗口色调完全由这里决定。
    /// </summary>
    public static void RegisterThemeRoot(FrameworkElement root)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_themeRoot, root))
                return;
            if (_themeRoot is not null)
                _themeRoot.ActualThemeChanged -= OnActualThemeChanged;
            _themeRoot = root;
            root.ActualThemeChanged += OnActualThemeChanged;
        }
        ApplyThemeToBrushes();
    }

    private static void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyThemeToBrushes();

    private static void ApplyThemeToBrushes()
    {
        var isDark = IsDarkTheme();
        var cardColor = isDark ? DarkCardColor : LightCardColor;
        var windowColor = isDark ? DarkWindowColor : LightWindowColor;
        lock (Gate)
        {
            CardBrushes.RemoveAll(weak => !weak.TryGetTarget(out _));
            foreach (var weak in CardBrushes)
            {
                if (weak.TryGetTarget(out var brush))
                    brush.Color = cardColor;
            }
            WindowBrushes.RemoveAll(weak => !weak.TryGetTarget(out _));
            foreach (var weak in WindowBrushes)
            {
                if (weak.TryGetTarget(out var brush))
                    brush.Color = windowColor;
            }
        }
    }

    private static bool IsDarkTheme()
    {
        lock (Gate)
        {
            return _themeRoot?.ActualTheme == ElementTheme.Dark;
        }
    }

    private static byte Alpha(double percent) =>
        (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100);
}
