namespace IDVBuff.Appearance;

public sealed record MapCardColors(RgbColor Fill, RgbColor Border, RgbColor Text, RgbColor SecondaryText);

public static class MapCardPalette
{
    // Stable category slots belong to map groups, independently of the user's accent.
    private static readonly (string LightFill, string LightBorder, string DarkFill, string DarkBorder)[] Slots =
    [
        ("#EFCBD4", "#B4234D", "#3A1722", "#FF809F"),
        ("#EFD0C7", "#B13A21", "#3A1C16", "#FF9275"),
        ("#EED8BB", "#A85B00", "#382414", "#FFB45B"),
        ("#E9DCAD", "#8A6800", "#32290E", "#E8C84E"),
        ("#DDE2B9", "#6C7300", "#282B12", "#C6D35C"),
        ("#CBE4D4", "#1F7A3F", "#143021", "#65D58B"),
        ("#C7E4DC", "#147363", "#12302B", "#5ED0B9"),
        ("#DDD2EF", "#6842A6", "#261B3A", "#B69AE9"),
        ("#E4CDEE", "#8038A5", "#2D1738", "#D899EF"),
        ("#EECBDD", "#9B2D70", "#35162B", "#E58AC0"),
        ("#EDCACD", "#9E3941", "#34191C", "#E68D94"),
        ("#E1D3C7", "#7C5234", "#2E2119", "#D1A27E")
    ];

    public static MapCardColors Resolve(ThemeSnapshot theme, int? slot, bool selected)
    {
        if (selected)
            return new(theme[ThemeToken.Selection], theme[ThemeToken.SelectionBorder],
                theme[ThemeToken.SelectionText], theme[ThemeToken.SelectionText]);
        if (theme.IsHighContrast || slot is null || slot < 0 || slot >= Slots.Length)
            return new(theme[ThemeToken.Card], theme[ThemeToken.Divider], theme[ThemeToken.Text], theme[ThemeToken.TextSecondary]);
        var palette = Slots[slot.Value];
        var fill = RgbColor.Parse(theme.IsDark ? palette.DarkFill : palette.LightFill);
        return new(fill, RgbColor.Parse(theme.IsDark ? palette.DarkBorder : palette.LightBorder),
            AccentPaletteGenerator.EnsureContrast(theme[ThemeToken.Text], fill, 4.5, theme.IsDark),
            AccentPaletteGenerator.EnsureContrast(theme[ThemeToken.TextSecondary], fill, 4.5, theme.IsDark));
    }
}
