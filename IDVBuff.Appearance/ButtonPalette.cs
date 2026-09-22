namespace IDVBuff.Appearance;

public enum ThemeButtonRole { Standard, Accent, Danger, Warning, Success }
public sealed record ButtonStateColors(RgbColor Fill, RgbColor Border, RgbColor Text);
public sealed record ButtonColors(ButtonStateColors Normal, ButtonStateColors Hover,
    ButtonStateColors Pressed, ButtonStateColors Disabled);

public static class ButtonPalette
{
    public static ButtonColors Resolve(ThemeSnapshot theme, ThemeButtonRole role)
    {
        var disabled = new ButtonStateColors(theme[ThemeToken.ControlDisabled],
            theme[ThemeToken.ControlBorderDisabled], theme[ThemeToken.TextDisabled]);
        if (role == ThemeButtonRole.Standard)
            return new(new(theme[ThemeToken.ControlFill], theme[ThemeToken.ButtonBorder], theme[ThemeToken.Text]),
                new(theme[ThemeToken.ControlHover], theme[ThemeToken.ButtonBorderHover], theme[ThemeToken.Text]),
                new(theme[ThemeToken.ControlPressed], theme[ThemeToken.ButtonBorderHover], theme[ThemeToken.Text]), disabled);
        if (role == ThemeButtonRole.Accent || theme.IsHighContrast)
            return new(Filled(theme[ThemeToken.Accent], theme[ThemeToken.OnAccent]),
                Filled(theme[ThemeToken.AccentHover], theme[ThemeToken.OnAccent]),
                Filled(theme[ThemeToken.AccentPressed], theme[ThemeToken.OnAccent]), disabled);
        var seed = role switch
        {
            ThemeButtonRole.Danger => theme[ThemeToken.ErrorText],
            ThemeButtonRole.Warning => theme[ThemeToken.WarningText],
            ThemeButtonRole.Success => theme[ThemeToken.SuccessText],
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
        var palette = AccentPaletteGenerator.Generate(seed, theme.IsDark, theme[ThemeToken.Card]);
        return new(Filled(palette.Fill, palette.OnFill), Filled(palette.Hover, palette.OnFill),
            Filled(palette.Pressed, palette.OnFill), disabled);
    }

    private static ButtonStateColors Filled(RgbColor fill, RgbColor text) => new(fill, fill, text);
}
