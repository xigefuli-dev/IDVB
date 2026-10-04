namespace IDVBuff.Appearance;

public static class ThemeContrastValidator
{
    public static void Validate(ThemeSnapshot snapshot)
    {
        foreach (var token in Enum.GetValues<ThemeToken>())
            if (!snapshot.Colors.ContainsKey(token))
                throw new ArgumentException($"主题缺少必要资源：{token}");
        // System contrast themes are user-controlled. Preserve their exact choices.
        if (snapshot.IsHighContrast) return;
        void Check(ThemeToken foreground, ThemeToken background, double minimum = 4.5)
        {
            var ratio = RgbColor.Contrast(snapshot[foreground], snapshot[background]);
            if (ratio < minimum) throw new ArgumentException($"主题对比度不足：{foreground}/{background}={ratio:F3}");
        }
        foreach (var surface in new[] { ThemeToken.Window, ThemeToken.Card, ThemeToken.Raised, ThemeToken.Dialog, ThemeToken.Flyout })
        {
            Check(ThemeToken.Text, surface);
            Check(ThemeToken.TextSecondary, surface);
            Check(ThemeToken.TextMuted, surface);
            Check(ThemeToken.AccentText, surface);
            Check(ThemeToken.Focus, surface, 3);
        }
        foreach (var fill in new[] { ThemeToken.Accent, ThemeToken.AccentHover, ThemeToken.AccentPressed }) Check(ThemeToken.OnAccent, fill);
        foreach (var fill in new[] { ThemeToken.Selection, ThemeToken.SelectionHover })
        {
            Check(ThemeToken.SelectionText, fill);
            Check(ThemeToken.SelectionBorder, fill, 3);
        }
        Check(ThemeToken.ControlBorder, ThemeToken.ControlFill, 3);
        Check(ThemeToken.TextSelectionText, ThemeToken.TextSelection);
        Check(ThemeToken.Text, ThemeToken.ControlHover);
        Check(ThemeToken.Text, ThemeToken.ControlPressed);
        Check(ThemeToken.SuccessText, ThemeToken.SuccessFill);
        Check(ThemeToken.WarningText, ThemeToken.WarningFill);
        Check(ThemeToken.ErrorText, ThemeToken.ErrorFill);
        Check(ThemeToken.InfoText, ThemeToken.InfoFill);
    }
}
