using System.Collections.Frozen;

namespace IDVBuff.Appearance;

public sealed record ThemeSnapshot(bool IsDark, bool IsHighContrast, ThemeMaterial RequestedMaterial,
    ThemeMaterial EffectiveMaterial, string? FallbackReason, FrozenDictionary<ThemeToken, RgbColor> Colors)
{
    public RgbColor this[ThemeToken token] => Colors[token];
}

public static class ThemeResolver
{
    public static ThemeSnapshot Resolve(AppearancePreferences preferences, SystemAppearance system,
        ThemeProfile profile = ThemeProfile.Application, ThemeRegistry? registry = null)
    {
        preferences.Validate();
        if (!Enum.IsDefined(profile)) throw new ArgumentException("无效的主题作用域配置。", nameof(profile));
        var definition = (registry ?? ThemeRegistry.BuiltIn).Get(preferences.ThemeId);
        var dark = profile != ThemeProfile.Application || (preferences.Mode switch
        {
            AppearanceMode.Dark => true,
            AppearanceMode.Light => false,
            _ => system.IsDark
        });
        var colors = (dark ? definition.Dark : definition.Light).ToDictionary();
        var accent = preferences.AccentSource switch
        {
            AccentSource.System => system.Accent,
            AccentSource.Custom => RgbColor.Parse(preferences.CustomAccent!),
            _ => dark ? definition.DarkAccent : definition.LightAccent
        };
        var palette = AccentPaletteGenerator.Generate(accent, dark, colors[ThemeToken.Card]);
        colors[ThemeToken.Accent] = palette.Fill;
        colors[ThemeToken.AccentHover] = palette.Hover;
        colors[ThemeToken.AccentPressed] = palette.Pressed;
        colors[ThemeToken.OnAccent] = palette.OnFill;
        var surfaces = new[] { ThemeToken.Window, ThemeToken.Card, ThemeToken.Raised, ThemeToken.Dialog, ThemeToken.Flyout }
            .Select(token => colors[token]).ToArray();
        var textSurface = dark ? surfaces.MaxBy(color => color.Luminance) : surfaces.MinBy(color => color.Luminance);
        colors[ThemeToken.AccentText] = AccentPaletteGenerator.EnsureContrast(palette.Text, textSurface, 4.5, dark);
        colors[ThemeToken.Selection] = palette.Selection;
        colors[ThemeToken.SelectionHover] = palette.SelectionHover;
        colors[ThemeToken.SelectionText] = palette.OnSelection;
        colors[ThemeToken.SelectionBorder] = palette.Border;
        // WinUI editable-text selection renders white glyphs in normal contrast mode.
        // Container selection uses a different, softer fill with its own text color.
        colors[ThemeToken.TextSelection] = AccentPaletteGenerator.EnsureContrast(accent, new(255, 255, 255), 4.5, false);
        colors[ThemeToken.TextSelectionText] = new(255, 255, 255);
        colors[ThemeToken.Focus] = AccentPaletteGenerator.EnsureContrast(palette.Border, textSurface, 3, dark);
        colors[ThemeToken.FocusInner] = colors[ThemeToken.Card];
        var hc = system.HighContrast;
        if (hc is not null)
        {
            foreach (var token in Enum.GetValues<ThemeToken>())
                colors[token] = IsForeground(token) ? hc.Foreground : hc.Background;
            foreach (var token in new[] { ThemeToken.Accent, ThemeToken.AccentHover, ThemeToken.AccentPressed,
                ThemeToken.Selection, ThemeToken.SelectionHover, ThemeToken.TextSelection }) colors[token] = hc.Highlight;
            foreach (var token in new[] { ThemeToken.OnAccent, ThemeToken.SelectionText, ThemeToken.TextSelectionText }) colors[token] = hc.HighlightText;
            foreach (var token in new[] { ThemeToken.Focus, ThemeToken.SelectionBorder }) colors[token] = hc.Highlight;
            colors[ThemeToken.TextDisabled] = hc.Disabled;
            colors[ThemeToken.AccentText] = hc.Link;
        }
        var material = preferences.Material;
        string? reason = null;
        if (hc is not null) { material = ThemeMaterial.Solid; reason = "HighContrast"; }
        else if (profile != ThemeProfile.Application) { material = ThemeMaterial.Solid; reason = "WorkspaceProfile"; }
        else if (!system.TransparencyEnabled) { material = ThemeMaterial.Solid; reason = "TransparencyDisabled"; }
        else if (!definition.Materials.Contains(material)) { material = ThemeMaterial.Solid; reason = "UnsupportedMaterial"; }
        var snapshot = new ThemeSnapshot(dark, hc is not null, preferences.Material, material, reason, colors.ToFrozenDictionary());
        ThemeContrastValidator.Validate(snapshot);
        return snapshot;
    }

    private static bool IsForeground(ThemeToken token) => token.ToString().Contains("Text", StringComparison.Ordinal)
        || token.ToString().Contains("Border", StringComparison.Ordinal)
        || token is ThemeToken.Divider or ThemeToken.Focus;

}
