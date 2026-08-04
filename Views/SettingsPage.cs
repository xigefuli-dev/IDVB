using System.Reflection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

/// <summary>Product, licensing, privacy, and attribution information.</summary>
public sealed class SettingsPage : Page
{
    private static Brush PrimaryTextBrush => FluentTheme.Brush("TextFillColorPrimaryBrush");
    private static Brush SecondaryTextBrush => FluentTheme.Brush("TextFillColorSecondaryBrush");
    private static Brush CardBorderBrush => FluentTheme.Brush("CardStrokeColorDefaultBrush");
    private static Brush AccentBrush => FluentTheme.Brush("AccentFillColorDefaultBrush");
    private static Brush AccentContainerBrush => FluentTheme.Brush("AccentFillColorTertiaryBrush");

    public SettingsPage() => Content = CreateContent();

    private FrameworkElement CreateContent()
    {
        var root = new StackPanel
        {
            Margin = new Thickness(40, 36, 40, 64),
            Spacing = 24,
            MaxWidth = 980
        };

        root.Children.Add(CreateHero());

        var informationGrid = new Grid { ColumnSpacing = 16 };
        informationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        informationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        informationGrid.Children.Add(CreateInfoCard(
            "作者与维护者",
            "@xigefuli · 对镜自演 · 精通骗人的骗术师",
            "三个署名均指向同一位作者。",
            Symbol.Contact));
        var versionCard = CreateInfoCard(
            "当前版本",
            GetDisplayVersion(),
            $"{(AppDataPaths.IsTestBuild ? "测试构建" : "公开构建")} · 仅支持 Windows x64",
            Symbol.Sync);
        Grid.SetColumn(versionCard, 1);
        informationGrid.Children.Add(versionCard);
        root.Children.Add(informationGrid);

        root.Children.Add(CreateSectionHeading(
            "许可与使用边界",
            "IDVB 源代码公开，允许个人学习、研究和非商业使用；它包含用途限制，因此不属于 OSI 定义的开源软件。"));

        var specifications = new StackPanel { Spacing = 12 };
        specifications.Children.Add(CreateSpecificationCard(
            "源码公开 · 禁止商用",
            "非商业许可",
            "可以查看、学习和为非商业目的修改；不得销售、收费分发、商业集成或用于营利服务。",
            Symbol.Document,
            "Identity Vision Bridge 非商业源码许可",
            "允许的行为\n\n"
            + "• 为个人学习、研究和非商业用途使用、复制及修改源代码\n"
            + "• 在保留版权、许可文本、作者署名并标明修改的前提下，非商业分发源代码或修改版本\n\n"
            + "禁止的行为\n\n"
            + "• 出售、出租、收费分发，或用于广告、订阅、代练、付费服务及其他直接或间接营利活动\n"
            + "• 移除或伪造作者、版权、许可及来源信息\n"
            + "• 将项目名称、图标或作者身份用于暗示官方认可\n\n"
            + "任何商业授权均须事先取得权利人的书面许可。完整条款以随软件和源码提供的 LICENSE 为准。"));
        specifications.Children.Add(CreateSpecificationCard(
            "禁止违法、侵权与破解",
            "用途限制",
            "不得用于作弊、破解游戏、绕过技术保护、攻击服务或侵犯他人著作权与隐私。",
            Symbol.BlockContact,
            "禁止用途",
            "不得使用本软件或其修改版本：\n\n"
            + "• 开发、传播或协助游戏外挂、作弊、自动化对局及不公平竞技工具\n"
            + "• 破解游戏或其他软件，绕过加密、反作弊、访问控制、付费或其他技术保护措施\n"
            + "• 未经授权访问、干扰、攻击服务器、账号、设备或网络\n"
            + "• 复制、提取、传播无权使用的游戏素材、地图、账号数据或其他受保护内容\n"
            + "• 实施任何违反适用法律、平台规则或侵害第三方合法权益的行为\n\n"
            + "项目公开不代表作者授权任何第三方游戏素材，也不代表游戏厂商认可或关联本项目。"));
        specifications.Children.Add(CreateSpecificationCard(
            "隐私与本地数据",
            "本地处理",
            "当前版本没有遥测或上传逻辑；识别在本机完成，设置、地图、日志与研究数据保存在本地。",
            Symbol.Permissions,
            "隐私与数据说明",
            "IDVB 会在功能开启时读取《第五人格》游戏窗口画面，用于本机地图识别与叠加显示。\n\n"
            + "• 设置、导入地图、识别缓存和日志存放在本机应用数据目录\n"
            + "• 常规识别不会把游戏截图写入磁盘\n"
            + "• 日志收集由用户控制，不会自动上传\n"
            + "• 只有显式开启“对齐研究数据”后，才可能保存视口截图与分析结果；默认保留 30 天，总量上限 2 GB\n"
            + "• 当前版本未实现账号系统、遥测、云同步或网络上传\n\n"
            + $"本地数据目录：{AppDataPaths.RootDirectory}\n卸载程序可让用户选择是否保留这些数据。分享日志或 IDVM 前请自行检查并移除敏感或无权传播的内容。"));
        specifications.Children.Add(CreateSpecificationCard(
            "第三方与免责声明",
            "非官方项目",
            "与网易、第五人格及其运营方无隶属或授权关系；第三方组件和素材分别受其权利与许可约束。",
            Symbol.Important,
            "第三方声明与免责声明",
            "Identity Vision Bridge 是独立的非官方社区项目，与网易、《第五人格》及其开发、发行或运营方不存在隶属、合作、赞助或认可关系。相关名称、商标、游戏画面和素材归各自权利人所有。\n\n"
            + "Microsoft Windows App SDK、WebView2、OpenCvSharp 等第三方组件适用各自的许可证，本项目许可不会改变这些条款。\n\n"
            + "本软件按“现状”提供，不承诺无错误、持续可用或适合特定目的。使用者应自行遵守法律、游戏用户协议和平台规则，并自行承担使用、修改或分发产生的风险。"));
        root.Children.Add(specifications);

        root.Children.Add(new Border
        {
            Margin = new Thickness(0, 4, 0, 0),
            Padding = new Thickness(16, 13, 16, 13),
            Background = FluentTheme.Brush("SystemFillColorCautionBackgroundBrush"),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = "许可摘要只为方便阅读，不替代完整 LICENSE。若摘要与完整条款不一致，以 LICENSE 为准；商业使用请先取得作者书面授权。",
                FontSize = 13,
                Foreground = PrimaryTextBrush,
                TextWrapping = TextWrapping.Wrap
            }
        });

        root.Children.Add(new TextBlock
        {
            Text = $"© 2026 XGFL。Identity Vision Bridge（IDVB）· Identity Vision Model（.idvm）",
            FontSize = 12,
            Foreground = SecondaryTextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
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
            Text = "面向桌面场景的实时可视化叠加层框架",
            FontSize = 14,
            Foreground = SecondaryTextBrush
        });
        identity.Children.Add(new TextBlock
        {
            Text = "管理和呈现 Identity Vision Model（IDVM）数据，提供本地视觉感知、坐标映射与实时叠加呈现能力。",
            FontSize = 13,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(14),
            Background = AccentContainerBrush,
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/Icons/IDVB_icon_square_master.png")),
                Stretch = Stretch.Uniform
            }
        });
        Grid.SetColumn(identity, 1);
        identity.Margin = new Thickness(18, 0, 0, 0);
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

    private static StackPanel CreateSectionHeading(string title, string description)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = PrimaryTextBrush
        });
        panel.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 14,
            Foreground = SecondaryTextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        return panel;
    }

    private static Border CreateInfoCard(string label, string value, string detail, Symbol icon)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new SymbolIcon(icon) { Foreground = AccentBrush });
        content.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = SecondaryTextBrush });
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
            MinHeight = 150,
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

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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
        var badge = new Border
        {
            MinHeight = 32,
            Padding = new Thickness(12, 5, 12, 5),
            Background = FluentTheme.Brush("SubtleFillColorSecondaryBrush"),
            BorderBrush = CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = state,
                FontSize = 14,
                Foreground = PrimaryTextBrush,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
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

    private static string GetDisplayVersion()
    {
        var assembly = typeof(SettingsPage).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+')[0];
        var fileVersion = assembly.GetName().Version?.ToString(4);
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? fileVersion ?? "未知版本"
            : $"{informationalVersion}（{fileVersion}）";
    }

    private async void SpecificationCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string> details })
            return;

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = details.Item1,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = details.Item2,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                }
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }
}
