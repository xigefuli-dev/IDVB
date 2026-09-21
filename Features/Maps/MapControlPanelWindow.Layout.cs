using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Features.Maps;

public sealed partial class MapControlPanelWindow
{
    private UIElement BuildContent()
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition
            { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 10, Margin = new Thickness(8) };
        header.Children.Add(new TextBlock
        {
            Text = "Identity Vision Bridge 对局控件",
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
        });
        header.Children.Add(_stateText);
        content.Children.Add(header);

        var mode = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(8, 10, 8, 8)
        };
        _classComboBox.SelectionChanged += ClassComboBox_SelectionChanged;
        mode.Children.Add(_classComboBox);
        Grid.SetRow(mode, 1);
        content.Children.Add(mode);

        var variants = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(8, 12, 8, 12)
        };
        Grid.SetRow(variants, 2);
        variants.Children.Add(_variantHeading);
        _variantScroller.Content = _variantButtons;
        variants.Children.Add(_variantScroller);
        content.Children.Add(variants);

        var actions = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(8, 8, 8, 8)
        };
        Grid.SetRow(actions, 3);
        actions.Children.Add(_messageText);
        actions.Children.Add(_beginButton);
        actions.Children.Add(_correctMapButton);
        actions.Children.Add(_endButton);
        content.Children.Add(actions);
        return content;
    }
}
