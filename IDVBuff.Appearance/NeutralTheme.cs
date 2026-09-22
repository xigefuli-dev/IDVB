namespace IDVBuff.Appearance;

internal static class NeutralTheme
{
    public static ThemeDefinition Definition { get; } = new(AppearancePreferences.DefaultThemeId, "IDVB 中性",
        CreateNeutral(false), CreateNeutral(true), RgbColor.Parse("#245DD8"), RgbColor.Parse("#8BB8FF"),
        [ThemeMaterial.Solid, ThemeMaterial.Frosted]);

    private static Dictionary<ThemeToken, RgbColor> CreateNeutral(bool dark)
    {
        var c = new Dictionary<ThemeToken, RgbColor>();
        void Set(ThemeToken token, string light, string night) => c[token] = RgbColor.Parse(dark ? night : light);
        // Traditional dark: neutral luminance layers, with color reserved for meaning.
        Set(ThemeToken.Window, "#F0F0F0", "#202020");
        Set(ThemeToken.Card, "#FAFAFA", "#2B2B2B");
        Set(ThemeToken.Raised, "#FFFFFF", "#353535");
        Set(ThemeToken.Dialog, "#FAFAFA", "#2B2B2B");
        Set(ThemeToken.Flyout, "#FFFFFF", "#353535");
        Set(ThemeToken.Text, "#202020", "#F5F5F5");
        Set(ThemeToken.TextSecondary, "#5C5C5C", "#C8C8C8");
        Set(ThemeToken.TextMuted, "#696969", "#AEAEAE");
        Set(ThemeToken.TextDisabled, "#767676", "#999999");
        Set(ThemeToken.Divider, "#DCDCDC", "#404040");
        Set(ThemeToken.SurfaceBorder, "#CCCCCC", "#4C4C4C");
        Set(ThemeToken.ButtonBorder, "#C4C4C4", "#565656");
        Set(ThemeToken.ButtonBorderHover, "#A6A6A6", "#707070");
        Set(ThemeToken.ControlFill, "#FFFFFF", "#373737");
        Set(ThemeToken.ControlHover, "#EDEDED", "#414141");
        Set(ThemeToken.ControlPressed, "#E4E4E4", "#303030");
        Set(ThemeToken.ControlDisabled, "#EAEAEA", "#303030");
        Set(ThemeToken.ControlBorder, "#858585", "#929292");
        Set(ThemeToken.ControlBorderHover, "#666666", "#B0B0B0");
        Set(ThemeToken.ControlBorderDisabled, "#C8C8C8", "#505050");
        Set(ThemeToken.SuccessFill, "#E9F5ED", "#17382B");
        Set(ThemeToken.SuccessText, "#19643A", "#A1E9BE");
        Set(ThemeToken.SuccessBorder, "#277344", "#65C891");
        Set(ThemeToken.WarningFill, "#FFF5DC", "#392D18");
        Set(ThemeToken.WarningText, "#704500", "#F4D18B");
        Set(ThemeToken.WarningBorder, "#936409", "#D2A645");
        Set(ThemeToken.ErrorFill, "#FCEBEA", "#402329");
        Set(ThemeToken.ErrorText, "#A1222A", "#FFB2BB");
        Set(ThemeToken.ErrorBorder, "#B12B36", "#F18494");
        Set(ThemeToken.InfoFill, "#EAF1FD", "#21364D");
        Set(ThemeToken.InfoText, "#214D86", "#AFD3FF");
        Set(ThemeToken.InfoBorder, "#3568A5", "#83B9F4");
        return c;
    }
}
