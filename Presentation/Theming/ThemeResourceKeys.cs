using IDVBuff.Appearance;

namespace IDVBuff.Presentation.Theming;

internal static class ThemeResourceKeys
{
    // Lightweight styling keys verified against the pinned WinUI 1.8 generic.xaml.
    // These are aliases, never a second palette.
    public static IReadOnlyDictionary<string, ThemeToken> Brushes { get; } = Create();

    private static Dictionary<string, ThemeToken> Create()
    {
        var keys = new Dictionary<string, ThemeToken>(StringComparer.Ordinal);
        void Add(ThemeToken token, params string[] names)
        {
            foreach (var name in names) keys[name] = token;
        }
        Add(ThemeToken.Text, "TextFillColorPrimaryBrush", "ControlStrongFillColorDefaultBrush", "ContentDialogForeground", "ContentDialogContentForegroundBrush", "ToolTipForeground");
        Add(ThemeToken.TextSecondary, "TextFillColorSecondaryBrush");
        Add(ThemeToken.TextMuted, "TextFillColorTertiaryBrush");
        Add(ThemeToken.TextDisabled, "TextFillColorDisabledBrush", "ControlStrongFillColorDisabledBrush");
        Add(ThemeToken.Window, "ApplicationPageBackgroundThemeBrush");
        Add(ThemeToken.Card, "CardBackgroundFillColorDefaultBrush", "LayerFillColorDefaultBrush");
        Add(ThemeToken.Raised, "LayerOnAcrylicFillColorDefaultBrush", "CardBackgroundFillColorSecondaryBrush");
        Add(ThemeToken.Divider, "CardStrokeColorDefaultBrush", "DividerStrokeColorDefaultBrush", "SurfaceStrokeColorDefaultBrush", "MenuFlyoutSeparatorBackground", "ContentDialogSeparatorBorderBrush");
        Add(ThemeToken.ControlFill, "ControlFillColorDefaultBrush", "ControlAltFillColorSecondaryBrush");
        Add(ThemeToken.ControlHover, "ControlFillColorSecondaryBrush", "SubtleFillColorSecondaryBrush", "ControlAltFillColorTertiaryBrush");
        Add(ThemeToken.ControlPressed, "ControlFillColorTertiaryBrush", "SubtleFillColorTertiaryBrush", "ControlAltFillColorQuarternaryBrush");
        Add(ThemeToken.ControlDisabled, "ControlFillColorDisabledBrush", "ControlAltFillColorDisabledBrush");
        Add(ThemeToken.ButtonBorder, "ControlStrokeColorDefaultBrush");
        Add(ThemeToken.ControlBorder, "ControlStrongStrokeColorDefaultBrush");
        Add(ThemeToken.ButtonBorderHover, "ControlStrokeColorSecondaryBrush");
        Add(ThemeToken.ControlBorderDisabled, "ControlStrokeColorDefaultDisabledBrush", "ControlStrongStrokeColorDisabledBrush");
        Add(ThemeToken.Accent, "AccentFillColorDefaultBrush");
        Add(ThemeToken.AccentHover, "AccentFillColorSecondaryBrush");
        Add(ThemeToken.AccentPressed, "AccentFillColorTertiaryBrush");
        Add(ThemeToken.OnAccent, "TextOnAccentFillColorPrimaryBrush", "TextOnAccentFillColorSecondaryBrush");
        Add(ThemeToken.AccentText, "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush", "HyperlinkForeground", "HyperlinkForegroundPointerOver", "HyperlinkForegroundPressed");
        Add(ThemeToken.Focus, "FocusStrokeColorOuterBrush", "SystemControlFocusVisualPrimaryBrush");
        Add(ThemeToken.FocusInner, "FocusStrokeColorInnerBrush", "SystemControlFocusVisualSecondaryBrush");
        Add(ThemeToken.ErrorText, "SystemFillColorCriticalBrush");
        Add(ThemeToken.ErrorFill, "SystemFillColorCriticalBackgroundBrush");
        Add(ThemeToken.WarningText, "SystemFillColorCautionBrush");
        Add(ThemeToken.WarningFill, "SystemFillColorCautionBackgroundBrush");
        Add(ThemeToken.SuccessText, "SystemFillColorSuccessBrush");
        Add(ThemeToken.SuccessFill, "SystemFillColorSuccessBackgroundBrush");
        Add(ThemeToken.Dialog, "ContentDialogBackground");
        Add(ThemeToken.Flyout, "ComboBoxDropDownBackground", "MenuFlyoutPresenterBackground", "FlyoutPresenterBackground", "ToolTipBackground");
        Add(ThemeToken.Flyout, "TeachingTipBackgroundBrush", "TeachingTipTransientBackground");
        Add(ThemeToken.SurfaceBorder, "TeachingTipBorderBrush");
        Add(ThemeToken.Text, "TeachingTipForegroundBrush", "TeachingTipTitleForegroundBrush");
        Add(ThemeToken.TextSecondary, "TeachingTipSubtitleForegroundBrush", "DropDownButtonForegroundSecondary",
            "DropDownButtonForegroundSecondaryPointerOver", "DropDownButtonForegroundSecondaryPressed");
        Add(ThemeToken.SurfaceBorder, "ComboBoxDropDownBorderBrush", "MenuFlyoutPresenterBorderBrush", "FlyoutPresenterBorderBrush", "ContentDialogBorderBrush", "ToolTipBorderBrush");
        Add(ThemeToken.Text, "ComboBoxDropDownForeground", "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ToggleSwitchContentForeground", "SliderHeaderForeground");
        Add(ThemeToken.TextDisabled, "ComboBoxDropDownGlyphForegroundDisabled", "ToggleSwitchContentForegroundDisabled", "SliderHeaderForegroundDisabled");
        Add(ThemeToken.ControlFill, "ComboBoxBackgroundFocused", "ComboBoxBackgroundUnfocused", "TextControlBackgroundFocused");
        Add(ThemeToken.Text, "ComboBoxForegroundFocused", "TextControlForegroundFocused", "TextControlHeaderForeground");
        Add(ThemeToken.TextSecondary, "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundFocused", "TextControlPlaceholderForegroundPointerOver");
        Add(ThemeToken.TextDisabled, "TextControlPlaceholderForegroundDisabled", "TextControlHeaderForegroundDisabled");
        Add(ThemeToken.Focus, "TextControlBorderBrushFocused", "ComboBoxBackgroundBorderBrushFocused");
        Add(ThemeToken.ControlBorder, "ComboBoxBackgroundBorderBrushUnfocused");
        Add(ThemeToken.TextSelection, "TextControlHighlighterBackground", "TextControlSelectionHighlightColor");
        Add(ThemeToken.TextSelectionText, "TextControlHighlighterForeground");

        foreach (var (suffix, fill, accent) in new[]
        {
            ("", ThemeToken.ControlFill, ThemeToken.Accent),
            ("PointerOver", ThemeToken.ControlHover, ThemeToken.AccentHover),
            ("Pressed", ThemeToken.ControlPressed, ThemeToken.AccentPressed),
            ("Disabled", ThemeToken.ControlDisabled, ThemeToken.ControlDisabled)
        })
        {
            var disabled = suffix == "Disabled";
            var foreground = disabled ? ThemeToken.TextDisabled : ThemeToken.Text;
            var border = disabled ? ThemeToken.ControlBorderDisabled : suffix == "" ? ThemeToken.ControlBorder : ThemeToken.ControlBorderHover;
            var buttonBorder = disabled ? ThemeToken.ControlBorderDisabled : suffix == "" ? ThemeToken.ButtonBorder : ThemeToken.ButtonBorderHover;
            foreach (var prefix in new[] { "Button", "ComboBox", "TextControl", "ToggleButton" })
            {
                Add(fill, prefix + "Background" + suffix);
                Add(foreground, prefix + "Foreground" + suffix);
                Add(prefix is "ComboBox" or "TextControl" ? border : buttonBorder, prefix + "BorderBrush" + suffix);
            }
            Add(accent, "AccentButtonBackground" + suffix, "AccentButtonBorderBrush" + suffix,
                "ToggleSwitchFillOn" + suffix, "ToggleSwitchStrokeOn" + suffix, "SliderThumbBackground" + suffix, "SliderTrackValueFill" + suffix);
            Add(disabled ? ThemeToken.TextDisabled : ThemeToken.OnAccent, "AccentButtonForeground" + suffix, "ToggleSwitchKnobFillOn" + suffix);
            Add(foreground, "TeachingTipAlternateCloseButtonForeground" + suffix);
            if (suffix is "PointerOver" or "Pressed")
            {
                Add(fill, "TeachingTipAlternateCloseButtonBackground" + suffix,
                    "TeachingTipAlternateCloseButtonBorderBrush" + suffix,
                    "AppBarButtonBackground" + suffix, "HyperlinkButtonBackground" + suffix);
            }
            Add(fill, "ToggleSwitchFillOff" + suffix);
            Add(border, "ToggleSwitchStrokeOff" + suffix, "ToggleSwitchKnobFillOff" + suffix, "SliderTrackFill" + suffix);
            Add(foreground, "RadioButtonForeground" + suffix);
            Add(fill, "RadioButtonOuterEllipseFill" + suffix);
            Add(border, "RadioButtonOuterEllipseStroke" + suffix);
            Add(accent, "RadioButtonOuterEllipseCheckedFill" + suffix, "RadioButtonOuterEllipseCheckedStroke" + suffix);
            Add(disabled ? ThemeToken.TextDisabled : ThemeToken.OnAccent, "RadioButtonCheckGlyphFill" + suffix,
                "RadioButtonCheckGlyphStroke" + suffix, "RadioButtonCheckGlyphStrokeChecked" + suffix);
            foreach (var prefix in new[] { "MenuFlyoutItem", "MenuFlyoutSubItem" })
            {
                Add(suffix == "" ? ThemeToken.Flyout : fill, prefix + "Background" + suffix);
                Add(foreground, prefix + "Foreground" + suffix);
            }
            foreach (var state in new[] { "Checked", "Indeterminate", "Unchecked" })
            {
                var selected = state != "Unchecked";
                // Only the check square receives the accent; its label stays on the parent surface.
                Add(selected ? accent : fill, "CheckBoxCheckBackgroundFill" + state + suffix);
                Add(selected ? accent : border, "CheckBoxCheckBackgroundStroke" + state + suffix);
                Add(foreground, "CheckBoxForeground" + state + suffix);
                Add(disabled ? ThemeToken.TextDisabled : ThemeToken.OnAccent, "CheckBoxCheckGlyphForeground" + state + suffix);
            }
            Add(disabled ? ThemeToken.ControlDisabled : suffix == "" ? ThemeToken.Selection : ThemeToken.SelectionHover,
                "ToggleButtonBackgroundChecked" + suffix, "ListViewItemBackgroundSelected" + suffix);
            Add(fill, "ToggleButtonBackgroundIndeterminate" + suffix);
            Add(foreground, "ToggleButtonForegroundIndeterminate" + suffix);
            Add(buttonBorder, "ToggleButtonBorderBrushIndeterminate" + suffix);
            Add(disabled ? ThemeToken.TextDisabled : ThemeToken.SelectionText, "ToggleButtonForegroundChecked" + suffix);
            Add(disabled ? ThemeToken.ControlBorderDisabled : ThemeToken.SelectionBorder, "ToggleButtonBorderBrushChecked" + suffix);
            if (!disabled)
            {
                Add(suffix == "" ? ThemeToken.Card : fill, "ListViewItemBackground" + suffix);
                Add(foreground, "ListViewItemForeground" + suffix);
                Add(ThemeToken.SelectionText, "ListViewItemForegroundSelected" + suffix);
            }
        }
        Add(ThemeToken.SelectionBorder, "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush", "ListViewItemSelectionIndicatorPressedBrush");
        return keys;
    }

    public static ThemeToken Resolve(string key)
    {
        if (Brushes.TryGetValue(key, out var token)) return token;
        if (key.StartsWith("Idvb", StringComparison.Ordinal) && key.EndsWith("Brush", StringComparison.Ordinal)
            && Enum.TryParse<ThemeToken>(key[4..^5], out token) && Enum.IsDefined(token)) return token;
        throw new ArgumentException($"未登记的主题资源：{key}", nameof(key));
    }
}
