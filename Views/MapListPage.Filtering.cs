using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private bool _updatingTagFilters;
    private Button CreateFilterButton()
    {
        var button = CreateSecondaryButton("筛选");
        button.MinWidth = 0;
        button.MinHeight = 45;
        button.Padding = new Thickness(14, 0, 14, 0);
        button.Flyout = CreateFilterFlyout();
        return button;
    }

    private Flyout CreateFilterFlyout()
    {
        var content = new StackPanel { Spacing = 0, MinWidth = 420 };
        var availableGroups = _filterGroups
            .Where(group => MapTagAuthorizationRules.IsAuthorized(
                group,
                _selectedClass,
                _loadedMaps))
            .ToArray();

        if (availableGroups.Length == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "当前 Class 没有可用标签组",
                TextWrapping = TextWrapping.Wrap
            });
        }

        for (var groupIndex = 0; groupIndex < availableGroups.Length; groupIndex++)
        {
            var group = availableGroups[groupIndex];
            if (groupIndex > 0)
            {
                content.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4, 0, 4),
                    Background = FluentTheme.Brush("DividerStrokeColorDefaultBrush")
                });
            }

            var groupContent = new StackPanel
            {
                Spacing = 8,
                Padding = new Thickness(0, 10, 0, 10)
            };
            groupContent.Children.Add(new TextBlock
            {
                Text = group.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var tags = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
            for (var column = 0; column < 3; column++)
                tags.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            for (var tagIndex = 0; tagIndex < group.Tags.Count; tagIndex++)
            {
                var tag = group.Tags[tagIndex];
                if (tagIndex % 3 == 0)
                    tags.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var checkBox = new CheckBox
                {
                    Content = tag,
                    MinWidth = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    IsChecked = _selectedTagFilters.TryGetValue(group.Id, out var selected)
                        && selected.Contains(tag)
                };
                checkBox.Checked += (_, _) => SetTagFilter(group.Id, tag, true);
                checkBox.Unchecked += (_, _) => SetTagFilter(group.Id, tag, false);
                Grid.SetRow(checkBox, tagIndex / 3);
                Grid.SetColumn(checkBox, tagIndex % 3);
                tags.Children.Add(checkBox);
            }
            groupContent.Children.Add(tags);
            content.Children.Add(groupContent);
        }

        var clear = CreateSecondaryButton("清除筛选");
        clear.HorizontalAlignment = HorizontalAlignment.Stretch;
        clear.Click += (_, _) =>
        {
            _updatingTagFilters = true;
            _selectedTagFilters.Clear();
            foreach (var checkBox in FindDescendants<CheckBox>(content))
                checkBox.IsChecked = false;
            _updatingTagFilters = false;
            RefreshMapCardsOnly();
        };
        content.Children.Add(new Border
        {
            BorderBrush = FluentTheme.Brush("DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 12, 0, 0),
            Child = clear
        });

        return new Flyout
        {
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content
            }
        };
    }

    private void SetTagFilter(Guid groupId, string tag, bool selected)
    {
        if (_updatingTagFilters)
            return;
        if (!_selectedTagFilters.TryGetValue(groupId, out var tags))
        {
            if (!selected)
                return;
            tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _selectedTagFilters[groupId] = tags;
        }

        if (selected)
            tags.Add(tag);
        else
        {
            tags.Remove(tag);
            if (tags.Count == 0)
                _selectedTagFilters.Remove(groupId);
        }
        RefreshMapCardsOnly();
    }

    private bool MatchesSelectedTagFilters(MapRecord map)
    {
        foreach (var (groupId, selectedTags) in _selectedTagFilters)
        {
            if (!map.Tags.TryGetValue(groupId, out var value)
                || !selectedTags.Contains(value))
                return false;
        }
        return true;
    }

    private void RefreshMapCardsOnly()
    {
        if (_mapCardsSurface is null)
            return;
        var maps = GetVisibleMaps();
        _selectedMapIds.RemoveWhere(id => maps.All(map => map.Id != id));
        _cardBorders.Clear();
        _mapCardsSurface.Child = CreateMapCardsContent(maps);
        UpdateSelectedCardVisuals();
    }

    private UIElement CreateMapCardsContent(IReadOnlyList<MapRecord> maps)
    {
        if (maps.Count == 0)
        {
            var empty = new Grid();
            empty.Children.Add(new TextBlock
            {
                Text = _selectedTagFilters.Count == 0 ? "尚未导入地图" : "没有符合筛选条件的地图",
                FontSize = 16,
                Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
            return empty;
        }

        var grid = new Grid { Margin = new Thickness(7, 12, 7, 12) };
        for (var column = 0; column < 3; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < maps.Count; index++)
        {
            if (index % 3 == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = CreateMapCard(maps[index]);
            Grid.SetRow(card, index / 3);
            Grid.SetColumn(card, index % 3);
            grid.Children.Add(card);
        }
        return grid;
    }

}
