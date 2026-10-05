using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Features.Maps;

public sealed partial class MapControlPanelWindow
{
    private readonly Dictionary<string, SolidColorBrush> _classThemeBrushes = new(StringComparer.Ordinal);
    private bool _classHasWarning;

    private void ApplyControlTheme()
    {
        _stateText.Foreground = ThemeService.For(_stateText)[ThemeToken.TextSecondary];
        _messageText.Foreground = ThemeService.For(_messageText)[ThemeToken.TextSecondary];
        _variantHeading.Foreground = ThemeService.For(_variantHeading)[ThemeToken.TextSecondary];
        ThemeButton.Apply(_beginButton, ThemeButtonRole.Standard);
        ThemeButton.Apply(_endButton, ThemeButtonRole.Standard);
        ThemeButton.Apply(_correctMapButton, ThemeButtonRole.Standard);
        ApplyClassTheme(hasWarning: false);
    }

    private void ApplyClassTheme(bool hasWarning)
    {
        _classHasWarning = hasWarning;
        RefreshClassTheme(ThemeService.For(_classComboBox).Snapshot);
    }

    private void OnClassThemeChanged(ThemeSnapshot snapshot) => RefreshClassTheme(snapshot);

    private void RefreshClassTheme(ThemeSnapshot snapshot)
    {
        void Set(string key, ThemeToken token)
        {
            if (!_classThemeBrushes.TryGetValue(key, out var brush))
            {
                brush = new SolidColorBrush();
                _classThemeBrushes.Add(key, brush);
                _classComboBox.Resources[key] = brush;
            }
            brush.Color = ThemeResources.ToColor(snapshot[token]);
        }

        var fill = _classHasWarning ? ThemeToken.WarningFill : ThemeToken.ControlFill;
        var border = _classHasWarning ? ThemeToken.WarningBorder : ThemeToken.ControlBorder;
        // Bind all native states to stable semantic brushes. Refresh only changes
        // their colors, including a currently hovered/focused control template.
        foreach (var (suffix, normalFill, normalBorder) in new[]
        {
            ("", ThemeToken.ControlFill, ThemeToken.ControlBorder),
            ("PointerOver", ThemeToken.ControlHover, ThemeToken.ControlBorderHover),
            ("Pressed", ThemeToken.ControlPressed, ThemeToken.ControlBorderHover),
            ("Disabled", ThemeToken.ControlDisabled, ThemeToken.ControlBorderDisabled)
        })
        {
            var disabled = suffix == "Disabled";
            Set("ComboBoxBackground" + suffix,
                _classHasWarning && !disabled ? ThemeToken.WarningFill : normalFill);
            Set("ComboBoxBorderBrush" + suffix,
                _classHasWarning && !disabled ? ThemeToken.WarningBorder : normalBorder);
            Set("ComboBoxForeground" + suffix,
                disabled ? ThemeToken.TextDisabled : ThemeToken.Text);
            Set("ComboBoxPlaceHolderForeground" + suffix,
                disabled ? ThemeToken.TextDisabled : ThemeToken.TextSecondary);
        }
        Set("ComboBoxBackgroundUnfocused", fill);
        Set("ComboBoxBackgroundFocused", fill);
        Set("ComboBoxBackgroundBorderBrushUnfocused", border);
        Set("ComboBoxBackgroundBorderBrushFocused", ThemeToken.Focus);
        Set("ComboBoxForegroundFocused", ThemeToken.Text);
        Set("ComboBoxForegroundFocusedPressed", ThemeToken.Text);
        Set("ComboBoxPlaceHolderForegroundFocused", ThemeToken.TextSecondary);
        Set("ComboBoxPlaceHolderForegroundFocusedPressed", ThemeToken.TextSecondary);
        Set("ComboBoxHeaderForeground", ThemeToken.Text);
        Set("ComboBoxHeaderForegroundDisabled", ThemeToken.TextDisabled);
        Set("ComboBoxDropDownGlyphForeground", ThemeToken.Text);
        Set("ComboBoxDropDownGlyphForegroundFocused", ThemeToken.Text);
        Set("ComboBoxDropDownGlyphForegroundFocusedPressed", ThemeToken.Text);
        Set("ComboBoxDropDownGlyphForegroundDisabled", ThemeToken.TextDisabled);
        _classComboBox.Background = _classThemeBrushes["ComboBoxBackground"];
        _classComboBox.BorderBrush = _classThemeBrushes["ComboBoxBorderBrush"];
        _classComboBox.Foreground = _classThemeBrushes["ComboBoxForeground"];
    }

    private static void ApplyVariantTheme(Button button, bool isCurrent)
    {
        ThemeButton.Apply(button, ThemeButtonRole.Standard);
        if (!isCurrent) return;
        var resources = ThemeService.For(button);
        // The current choice cannot be clicked, but remains visibly selected.
        button.Background = resources[ThemeToken.Selection];
        button.BorderBrush = resources[ThemeToken.SelectionBorder];
        button.Foreground = resources[ThemeToken.SelectionText];
        button.Resources["ButtonBackgroundDisabled"] = resources[ThemeToken.Selection];
        button.Resources["ButtonBorderBrushDisabled"] = resources[ThemeToken.SelectionBorder];
        button.Resources["ButtonForegroundDisabled"] = resources[ThemeToken.SelectionText];
    }
}
