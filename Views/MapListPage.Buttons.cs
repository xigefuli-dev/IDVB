using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private Button CreateSecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            BorderThickness = new Thickness(1),
            FontSize = 13,
            MinWidth = 98,
            MinHeight = 38,
            Padding = new Thickness(16, 6, 16, 6),
            CornerRadius = new CornerRadius(7)
        };
        AttachHoverFeedback(button);
        return button;
    }

    private static TeachingTip CreatePackageActionTeachingTip(
        Button target,
        string title,
        string subtitle,
        UIElement content) => new()
    {
        Target = target,
        Title = title,
        Subtitle = subtitle,
        Content = content,
        IsLightDismissEnabled = true,
        PreferredPlacement = TeachingTipPlacementMode.Bottom
    };

    private static Button CreateTeachingTipChoiceButton(string text) => new()
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        MinWidth = 150
    };

    private static Button CreateClassUtilityButton(Symbol symbol, IDVBuff.Appearance.ThemeButtonRole role) => ThemeButton.Apply(new Button
    {
        Content = new SymbolIcon(symbol),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        MinWidth = 0,
        MinHeight = 0,
        Padding = new Thickness(0),
        CornerRadius = new CornerRadius(4)
    }, role);

    private static Button CreateActionButton(string text, IDVBuff.Appearance.ThemeButtonRole role)
    {
        var button = new Button
        {
            Content = text,
            FontSize = 14,
            MinWidth = 108,
            MinHeight = 45,
            Padding = new Thickness(20, 7, 20, 7),
            CornerRadius = new CornerRadius(8)
        };
        AttachHoverFeedback(button);
        return ThemeButton.Apply(button, role);
    }
}
