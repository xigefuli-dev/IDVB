using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

/// <summary>
/// Application information, licensing, and release-readiness guidance.
/// </summary>
public sealed class SettingsPage : Page
{
    private static Brush PrimaryTextBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush SecondaryTextBrush => FluentTheme.Brush("TextFillColorSecondaryBrush");
    private static Brush CardBorderBrush => FluentTheme.Brush("CardStrokeColorDefaultBrush");
    private static Brush AccentBrush => FluentTheme.Brush("AccentFillColorDefaultBrush");
    private static Brush AccentContainerBrush => FluentTheme.Brush("AccentFillColorTertiaryBrush");

    public SettingsPage()
    {
        Content = CreateContent();
    }

    private FrameworkElement CreateContent()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(40, 36, 40, 64),
            Spacing = 28,
            MaxWidth = 980
        };

        root.Children.Add(CreateHero());

        var informationGrid = new Grid { ColumnSpacing = 16 };
        informationGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        informationGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });

        informationGrid.Children.Add(CreateInfoCard(
            "作者",
            "@xigefuli | 对镜自演 | 精通骗人的骗术师",
            "三个署名均指向同一位作者。",
            Symbol.Contact,
            AccentBrush));
        var versionCard = CreateInfoCard(
            "当前版本",
            "b01-26.8.4.1037",
            "内测构建 · Identity Vision Bridge",
            Symbol.Sync,
            AccentBrush);
        Grid.SetColumn(versionCard, 1);
        informationGrid.Children.Add(versionCard);
        root.Children.Add(informationGrid);

        root.Children.Add(new TextBlock
        {
            Text = "许可与发布规范",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        root.Children.Add(new TextBlock
        {
            Text = "以下内容用于集中管理开源发布前需要确认的约定。选择任一项目可查看应补充的具体信息。",
            FontSize = 14,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var specifications = new StackPanel { Spacing = 12 };
        specifications.Children.Add(CreateSpecificationCard(
            "开源许可",
            "待补全",
            "明确许可证类型、版权所有者与适用范围，再随源代码一同发布。",
            Symbol.Document,
            AccentBrush,
            "开源许可待补全",
            "请在公开仓库发布前补充：\n\n• 许可证全文与 SPDX 标识\n• 版权所有者及年份\n• 源代码、文档和资源的适用范围\n• 第三方依赖及其许可证清单"));
        specifications.Children.Add(CreateSpecificationCard(
            "隐私与数据使用",
            "待补全",
            "说明本地数据、日志、截图与研究数据的保存、导出和清理规则。",
            Symbol.Permissions,
            AccentBrush,
            "隐私与数据使用待补全",
            "建议在发布前说明：\n\n• 哪些数据只保存在本机\n• 日志或研究数据的启用条件\n• 数据导出、保留期限和清理方式\n• 是否存在任何网络传输或遥测"));
        specifications.Children.Add(CreateSpecificationCard(
            "贡献与行为规范",
            "待补全",
            "建立 Issue、提交、评审和社区交流的统一约定。",
            Symbol.People,
            AccentBrush,
            "贡献与行为规范待补全",
            "建议在发布前补充：\n\n• 贡献指南和本地验证要求\n• Issue 与拉取请求模板\n• 行为准则与沟通边界\n• 安全问题的私下反馈渠道"));
        specifications.Children.Add(CreateSpecificationCard(
            "版本与兼容性规范",
            "待补全",
            "定义版本号、IDVM 格式兼容性、升级策略和变更记录规则。",
            Symbol.Important,
            AccentBrush,
            "版本与兼容性规范待补全",
            "建议在发布前明确：\n\n• 版本号的组成与递增规则\n• IDVM 格式的兼容性承诺\n• 破坏性变更和迁移策略\n• 面向用户的更新日志格式"));
        root.Children.Add(specifications);

        root.Children.Add(new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(16, 13, 16, 13),
            Background = FluentTheme.Brush("SystemFillColorCautionBackgroundBrush"),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "提示：标记为“待补全”的内容不会改变现有功能；它们是公开发布前需要确认的文档与治理事项。",
                FontSize = 13,
                Foreground = SecondaryTextBrush,
                TextWrapping = TextWrapping.Wrap
            }
        });

        return root;
    }

    private static Border CreateHero()
    {
        var identity = new StackPanel { Spacing = 6 };
        identity.Children.Add(new TextBlock
        {
            Text = "Identity Vision Bridge",
            FontSize = 29,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        identity.Children.Add(new TextBlock
        {
            Text = "关于、许可与发布规范",
            FontSize = 14,
            Foreground = SecondaryTextBrush
        });

        var mark = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(12),
            Background = AccentContainerBrush,
            Child = new SymbolIcon(Symbol.Setting)
            {
                Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.Children.Add(mark);
        Grid.SetColumn(identity, 1);
        identity.Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(identity);

        return new Border
        {
            Padding = new Thickness(22),
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = grid
        };
    }

    private static Border CreateInfoCard(
        string label,
        string value,
        string detail,
        Symbol icon,
        Brush accent)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new SymbolIcon(icon)
        {
            Foreground = accent
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = SecondaryTextBrush
        });
        content.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = detail,
            FontSize = 12,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        return new Border
        {
            Padding = new Thickness(18),
            MinHeight = 160,
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = content
        };
    }

    private Button CreateSpecificationCard(
        string title,
        string state,
        string description,
        Symbol icon,
        Brush accent,
        string dialogTitle,
        string dialogContent)
    {
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });

        var badge = new Border
        {
            Padding = new Thickness(9, 4, 9, 4),
            Background = AccentContainerBrush,
            CornerRadius = new CornerRadius(999),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = state,
                FontSize = 12,
                Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(8),
            Background = AccentContainerBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new SymbolIcon(icon)
            {
                Foreground = FluentTheme.Brush("TextOnAccentFillColorPrimaryBrush")
            }
        });
        Grid.SetColumn(text, 1);
        text.Margin = new Thickness(14, 0, 16, 0);
        grid.Children.Add(text);
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(18, 16, 18, 16),
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Content = grid,
            Tag = (dialogTitle, dialogContent)
        };
        button.Click += SpecificationCard_Click;
        return button;
    }

    private async void SpecificationCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string> details })
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = details.Item1,
            Content = new TextBlock
            {
                Text = details.Item2,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }
}
