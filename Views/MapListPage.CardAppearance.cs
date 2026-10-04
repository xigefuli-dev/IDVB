using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private void UpdateSelectedCardVisuals()
    {
        var theme = FluentTheme.Snapshot(this);
        foreach (var (id, card) in _cardBorders)
        {
            var selected = _selectedMapIds.Contains(id);
            var group = _variantGroups.FirstOrDefault(candidate => candidate.MapIds.Contains(id));
            var colors = IDVBuff.Appearance.MapCardPalette.Resolve(theme, group?.PaletteSlot, selected);
            card.Background = new SolidColorBrush(ThemeResources.ToColor(colors.Fill));
            card.BorderBrush = new SolidColorBrush(ThemeResources.ToColor(colors.Border));
            if (card.Child is Grid content)
                foreach (var label in content.Children.OfType<TextBlock>())
                    label.Foreground = new SolidColorBrush(ThemeResources.ToColor(
                        Grid.GetRow(label) == 1 ? colors.Text : colors.SecondaryText));
        }

        if (_editButton is not null)
            _editButton.IsEnabled = HasSelection;
        if (_deleteButton is not null)
            _deleteButton.IsEnabled = HasSelection;
        if (_variantButton is not null)
            _variantButton.IsEnabled = _selectedMapIds.Count >= 2;
    }

}
