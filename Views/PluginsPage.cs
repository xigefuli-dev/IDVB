using IDVBuff.PluginContracts;
using IDVBuff.Features.Plugins;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;

namespace IDVBuff.Views;

/// <summary>
/// 插件管理页：列出宿主已注册的插件卡片（名称 / Id / 版本 / 描述 / 订阅消息）。
/// </summary>
public sealed class PluginsPage : Page
{
    private static Brush PrimaryTextBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush SecondaryTextBrush => FluentTheme.Brush("TextFillColorSecondaryBrush");

    public PluginsPage()
    {
        Content = CreateContent();
    }

    private FrameworkElement CreateContent()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(40, 36, 40, 64),
            Spacing = 24,
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "插件",
                    FontSize = 28,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = PrimaryTextBrush
                },
                new TextBlock
                {
                    Text = "已注册的插件服务。每个插件独立管理生命周期，宿主统一驱动。",
                    FontSize = 14,
                    Foreground = SecondaryTextBrush
                }
            }
        });

        var manager = App.Plugins;
        var plugins = manager?.Plugins ?? [];
        if (plugins.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "尚未注册任何插件。",
                FontSize = 14,
                Foreground = SecondaryTextBrush
            });
            return root;
        }

        root.Children.Add(new TextBlock
        {
            Text = $"{plugins.Count} 个插件",
            FontSize = 14,
            Foreground = SecondaryTextBrush
        });

        var cards = new Grid
        {
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < (plugins.Count + 1) / 2; row++)
            cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < plugins.Count; index++)
        {
            var card = CreatePluginCard(plugins[index], manager!);
            Grid.SetColumn(card, index % 2);
            Grid.SetRow(card, index / 2);
            cards.Children.Add(card);
        }
        root.Children.Add(cards);
        return root;
    }

    private static Border CreatePluginCard(IPlugin plugin, PluginManager manager)
    {
        var metadata = plugin.GetType().GetCustomAttribute<PluginAttribute>();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconSurface = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(8),
            Background = FluentTheme.Brush("AccentFillColorTertiaryBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new SymbolIcon(Symbol.AllApps)
            {
                Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
            }
        };
        grid.Children.Add(iconSurface);

        var body = new StackPanel
        {
            Margin = new Thickness(16, 0, 0, 0),
            Spacing = 6
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = plugin.DisplayName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(metadata?.Version))
        {
            titleRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 226, 239, 255)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = $"v{metadata.Version}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(
                        Windows.UI.Color.FromArgb(255, 0, 90, 158))
                }
            });
        }
        body.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(metadata?.Description))
        {
            body.Children.Add(new TextBlock
            {
                Text = metadata.Description,
                FontSize = 13,
                Foreground = SecondaryTextBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = $"ID: {plugin.Id}",
            FontSize = 11,
            Foreground = SecondaryTextBrush
        });

        var messageTypes = MessageBus.GetHandlerMessageTypes(plugin);
        if (messageTypes.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"订阅消息：{string.Join("、", messageTypes.Select(type => type.Name))}",
                FontSize = 11,
                Foreground = SecondaryTextBrush,
                TextWrapping = TextWrapping.Wrap
            });
        }

        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var toggle = new ToggleSwitch
        {
            IsOn = manager.IsEnabled(plugin.Id),
            Tag = plugin.Id,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 44,
            Margin = new Thickness(16, 0, 0, 0)
        };
        var changing = false;
        toggle.Toggled += (_, _) =>
        {
            if (changing)
                return;
            try
            {
                manager.SetEnabled(plugin.Id, toggle.IsOn);
            }
            catch
            {
                changing = true;
                toggle.IsOn = manager.IsEnabled(plugin.Id);
                changing = false;
            }
        };
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        return new Border
        {
            Padding = new Thickness(20),
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }
}
