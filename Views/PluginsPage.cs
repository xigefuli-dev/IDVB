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
/// 卡片右上角「···」按钮打开该插件的设置页——由 TTM 统一管理的 TeachingTip。
/// </summary>
public sealed partial class PluginsPage : Page
{
    private static Brush PrimaryTextBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush SecondaryTextBrush => FluentTheme.Brush("TextFillColorSecondaryBrush");

    /// <summary>root Grid，同时是 TTM 的 tip 宿主（overlay 槽）。</summary>
    private Panel? _tipHost;
    private StackPanel? _thirdPartyContainer;
    private InfoBar? _thirdPartyNotice;

    public PluginsPage()
    {
        Content = CreateContent();
        Loaded += PluginsPage_Loaded;
        Unloaded += PluginsPage_Unloaded;
    }

    private void PluginsPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_tipHost is not null)
            App.TeachingTips?.Attach(_tipHost);
        if (App.PluginNotifications is not null)
            App.PluginNotifications.NotificationPosted += PluginNotifications_NotificationPosted;
        _ = RefreshThirdPartyPluginsAsync();
    }

    private void PluginsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        // 页面离开导航树前摘除并关闭 tip，避免悬垂的 TeachingTip 引用本页元素。
        App.TeachingTips?.Close();
        if (App.PluginNotifications is not null)
            App.PluginNotifications.NotificationPosted -= PluginNotifications_NotificationPosted;
    }

    private FrameworkElement CreateContent()
    {
        // root 是 Grid：child[0] 为页面内容，TTM 把 TeachingTip 加为最后一个
        // child（同 MapListPage 的导入提示先例），作 overlay 使用。
        var root = new Grid();
        _tipHost = root;

        var content = new StackPanel
        {
            Margin = new Thickness(40, 36, 40, 64),
            Spacing = 24,
            MaxWidth = 1040,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(content);

        content.Children.Add(new StackPanel
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
            content.Children.Add(new TextBlock
            {
                Text = "尚未注册任何插件。",
                FontSize = 14,
                Foreground = SecondaryTextBrush
            });
        }
        else
        {
            content.Children.Add(new TextBlock
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
            content.Children.Add(cards);
        }

        AddThirdPartySection(content);
        return root;
    }

    private static Border CreatePluginCard(IPlugin plugin, PluginManager manager)
    {
        var metadata = plugin.GetType().GetCustomAttribute<PluginAttribute>();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
                // 关闭插件前先关掉它的设置页，避免 TTM 继续引用已停用插件。
                // 用 Dismiss（不置空 _host）：页面仍存活，Close 会令其余设置按钮失效。
                if (!toggle.IsOn && App.TeachingTips?.IsShowing(plugin.Id) == true)
                    App.TeachingTips?.Dismiss();
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

        // 右上角「···」设置按钮：仅当插件声明了设置描述符时出现。
        if (plugin is IPluginSettingsProvider)
        {
            var settingsButton = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 14 },
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(4),
                Background = FluentTheme.Brush("SubtleFillColorSecondaryBrush"),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(14, -6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            ToolTipService.SetToolTip(settingsButton, "插件设置");
            settingsButton.Click += (_, _) =>
                App.TeachingTips?.ShowSettings(plugin, settingsButton);
            Grid.SetColumn(settingsButton, 3);
            grid.Children.Add(settingsButton);
        }

        return new Border
        {
            Padding = new Thickness(20),
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }
}
