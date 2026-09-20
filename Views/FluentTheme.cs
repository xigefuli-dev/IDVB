using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace IDVBuff.Views;

internal static class FluentTheme
{
    public static bool UseLegacyTheme { get; private set; }

    public static SystemBackdrop? CreateWindowBackdrop(bool useLegacyTheme)
    {
        UseLegacyTheme = useLegacyTheme;
        return useLegacyTheme ? null : new GaussianBlurBackdrop();
    }

    public static Brush Brush(string resourceKey)
    {
        if (TryResolveManagedColor(resourceKey, IsDarkTheme(), out var color))
        {
            var brush = new SolidColorBrush(color);
            lock (Gate)
            {
                ManagedBrushes.RemoveAll(entry => !entry.Brush.TryGetTarget(out _));
                ManagedBrushes.Add(new ManagedBrushReference(
                    resourceKey,
                    new WeakReference<SolidColorBrush>(brush)));
            }
            return brush;
        }

        return Application.Current.Resources[resourceKey] as Brush
            ?? throw new InvalidOperationException($"Missing WinUI theme resource '{resourceKey}'.");
    }

    // These are deliberately kept here instead of scattered through the pages so the
    // acrylic balance can be tuned in one place. The alpha channel is the requested
    // fill opacity; the effective theme (root element's ActualTheme) selects the
    // corresponding light/dark color, matching the acrylic backdrop.
    // Tune these as ordinary percentages (0-100), independently per theme.
    // Light acrylic needs enough tint to stay clean without hiding the blurred
    // desktop entirely. 94/72 made the modern theme indistinguishable from the
    // legacy solid theme; these values preserve a visible translucent hierarchy.
    private const double LightWindowFillPercent = 82;
    private const double DarkWindowFillPercent = 45;
    private const double LightCardFillPercent = 28;
    private const double DarkCardFillPercent = 10;

    private static readonly Color LightWindowColor = Color.FromArgb(Alpha(LightWindowFillPercent), 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkWindowColor = Color.FromArgb(Alpha(DarkWindowFillPercent), 0x00, 0x00, 0x00);
    private static readonly Color LightCardColor = Color.FromArgb(Alpha(LightCardFillPercent), 0xFF, 0xFF, 0xFF);
    private static readonly Color DarkCardColor = Color.FromArgb(Alpha(DarkCardFillPercent), 0x2D, 0x2D, 0x2D);

    private static readonly List<WeakReference<SolidColorBrush>> CardBrushes = new();
    private static readonly List<WeakReference<SolidColorBrush>> WindowBrushes = new();
    private static readonly List<ManagedBrushReference> ManagedBrushes = new();
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

    public static void ApplyColorTheme(bool followSystemTheme, bool useDarkTheme)
    {
        FrameworkElement? themeRoot;
        lock (Gate)
            themeRoot = _themeRoot;

        if (themeRoot is null)
            return;

        themeRoot.RequestedTheme = followSystemTheme
            ? ElementTheme.Default
            : useDarkTheme
                ? ElementTheme.Dark
                : ElementTheme.Light;
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
            ManagedBrushes.RemoveAll(entry => !entry.Brush.TryGetTarget(out _));
            foreach (var entry in ManagedBrushes)
            {
                if (entry.Brush.TryGetTarget(out var brush)
                    && TryResolveManagedColor(entry.ResourceKey, isDark, out var color))
                {
                    brush.Color = color;
                }
            }
        }
    }

    // Application.Current.Resources resolves a programmatic lookup against the
    // application/system theme, not necessarily against a Page.RequestedTheme
    // override. Pages built in C# therefore used to retain dark-theme brushes
    // after the shell switched to light. Keep the small set used by those pages
    // here and mutate each brush with the registered root's ActualTheme.
    private static bool TryResolveManagedColor(string resourceKey, bool isDark, out Color color)
    {
        color = resourceKey switch
        {
            "TextFillColorPrimaryBrush" => isDark ? Hex("FFFFFFFF") : Hex("E4000000"),
            "TextFillColorSecondaryBrush" => isDark ? Hex("C5FFFFFF") : Hex("9E000000"),
            "ControlFillColorDefaultBrush" => isDark ? Hex("0FFFFFFF") : Hex("B3FFFFFF"),
            "ControlFillColorSecondaryBrush" => isDark ? Hex("15FFFFFF") : Hex("80F9F9F9"),
            "ControlFillColorDisabledBrush" => isDark ? Hex("0BFFFFFF") : Hex("4DF9F9F9"),
            "SubtleFillColorSecondaryBrush" => isDark ? Hex("0FFFFFFF") : Hex("09000000"),
            "ControlStrokeColorDefaultBrush" => isDark ? Hex("12FFFFFF") : Hex("0F000000"),
            "CardStrokeColorDefaultBrush" => isDark ? Hex("19000000") : Hex("0F000000"),
            "CardBackgroundFillColorDefaultBrush" => isDark ? Hex("0DFFFFFF") : Hex("B3FFFFFF"),
            "LayerFillColorDefaultBrush" => isDark ? Hex("4C3A3A3A") : Hex("80FFFFFF"),
            "ApplicationPageBackgroundThemeBrush" => isDark ? Hex("FF202020") : Hex("FFF3F3F3"),
            "SystemFillColorCriticalBrush" => isDark ? Hex("FFFF99A4") : Hex("FFC42B1C"),
            "SystemFillColorCautionBackgroundBrush" => isDark ? Hex("FF433519") : Hex("FFFFF4CE"),
            "TextOnAccentFillColorPrimaryBrush" => isDark ? Hex("FF000000") : Hex("FFFFFFFF"),
            "AccentFillColorDefaultBrush" => GetAccentFill(isDark, 1d),
            "AccentFillColorSecondaryBrush" => GetAccentFill(isDark, 0.9d),
            "AccentFillColorTertiaryBrush" => GetAccentFill(isDark, 0.8d),
            _ => default
        };
        return resourceKey is
            "TextFillColorPrimaryBrush" or
            "TextFillColorSecondaryBrush" or
            "ControlFillColorDefaultBrush" or
            "ControlFillColorSecondaryBrush" or
            "ControlFillColorDisabledBrush" or
            "SubtleFillColorSecondaryBrush" or
            "ControlStrokeColorDefaultBrush" or
            "CardStrokeColorDefaultBrush" or
            "CardBackgroundFillColorDefaultBrush" or
            "LayerFillColorDefaultBrush" or
            "ApplicationPageBackgroundThemeBrush" or
            "SystemFillColorCriticalBrush" or
            "SystemFillColorCautionBackgroundBrush" or
            "TextOnAccentFillColorPrimaryBrush" or
            "AccentFillColorDefaultBrush" or
            "AccentFillColorSecondaryBrush" or
            "AccentFillColorTertiaryBrush";
    }

    private static Color GetAccentFill(bool isDark, double opacity)
    {
        Color accent;
        try
        {
            accent = new UISettings().GetColorValue(
                isDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        }
        catch
        {
            accent = Color.FromArgb(255, 0, 120, 212);
        }

        return Color.FromArgb(
            (byte)Math.Round(accent.A * Math.Clamp(opacity, 0d, 1d)),
            accent.R,
            accent.G,
            accent.B);
    }

    private static Color Hex(string value) => Color.FromArgb(
        Convert.ToByte(value[..2], 16),
        Convert.ToByte(value.Substring(2, 2), 16),
        Convert.ToByte(value.Substring(4, 2), 16),
        Convert.ToByte(value.Substring(6, 2), 16));

    private static bool IsDarkTheme()
    {
        lock (Gate)
        {
            return _themeRoot?.ActualTheme == ElementTheme.Dark;
        }
    }

    private static byte Alpha(double percent) =>
        (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100);

    private sealed record ManagedBrushReference(
        string ResourceKey,
        WeakReference<SolidColorBrush> Brush);
}
