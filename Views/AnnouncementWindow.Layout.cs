using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using IDVBuff.Features.Announcements;

namespace IDVBuff.Views;

public sealed partial class AnnouncementWindow
{
    private FrameworkElement BuildLayout()
    {
        // 最外层 Border：带有细边框与微圆角，绝对无任何原生或自制标题栏
        var rootBorder = _themeRoot;
        rootBorder.Background = FluentTheme.CardBrush(_themeRoot);
        rootBorder.BorderBrush = FluentTheme.Brush(_themeRoot, "CardStrokeColorDefaultBrush");
        rootBorder.BorderThickness = new Thickness(0);
        rootBorder.CornerRadius = new CornerRadius(8);

        // 纯粹左右分栏：直接顶到窗口最顶端，没有任何多余的标题栏行
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(216, GridUnitType.Pixel) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootBorder.Child = contentGrid;

        // -------------------- 左侧栏 --------------------
        var leftPanel = new Grid
        {
            Background = FluentTheme.Brush(_themeRoot, "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 左侧只承担导航职责，尽量把宽度留给正文。
        var categoryBar = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(18, 19, 14, 18),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        categoryBar.Children.Add(new TextBlock
        {
            Text = "消息",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = FluentTheme.Brush(_themeRoot, "TextFillColorPrimaryBrush"),
        });

        Grid.SetRow(categoryBar, 0);
        leftPanel.Children.Add(categoryBar);

        // 列表区
        var listContainer = new Grid();
        listContainer.Children.Add(_announcementsListView);
        listContainer.Children.Add(_loadingRing);
        listContainer.Children.Add(_emptyTextBlock);
        Grid.SetRow(listContainer, 1);
        leftPanel.Children.Add(listContainer);

        Grid.SetColumn(leftPanel, 0);
        contentGrid.Children.Add(leftPanel);

        // -------------------- 右侧详情阅读区 --------------------
        var rightPanel = new Grid();
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 右侧 Header：分类徽章、标题、元信息与极简关闭按钮。
        // 公告窗口位置固定，Header 仅用于阅读，不能作为拖动区。
        var headerPanel = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(28, 20, 18, 18),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerStack = new StackPanel { Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        headerStack.Children.Add(_detailTitleBlock);
        var metaRow = new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.Children.Add(_categoryBadge);
        Grid.SetColumn(_detailMetaBlock, 1);
        metaRow.Children.Add(_detailMetaBlock);
        headerStack.Children.Add(metaRow);
        Grid.SetColumn(headerStack, 0);
        headerPanel.Children.Add(headerStack);

        // 右上角极简关闭按钮
        var closeButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE8BB", // ChromeClose
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
            },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        closeButton.Click += (_, _) => _appWindow.Hide();
        Grid.SetColumn(closeButton, 1);
        headerPanel.Children.Add(closeButton);

        Grid.SetRow(headerPanel, 0);
        rightPanel.Children.Add(headerPanel);

        // 右侧内容区：原生 Markdown 阅读器
        Grid.SetRow(_markdownView, 1);
        rightPanel.Children.Add(_markdownView);

        Grid.SetColumn(rightPanel, 1);
        contentGrid.Children.Add(rightPanel);

        return rootBorder;
    }

    private ListViewItem CreateAnnouncementListItem(AnnouncementItem item)
    {
        var card = new Grid { Padding = new Thickness(16, 13, 10, 13), ColumnSpacing = 8 };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 未读红点
        var unreadDot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = FluentTheme.Brush(_themeRoot, "AccentFillColorDefaultBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = item.IsRead ? Visibility.Collapsed : Visibility.Visible,
        };
        Grid.SetColumn(unreadDot, 0);
        card.Children.Add(unreadDot);

        var contentStack = new StackPanel { Spacing = 6 };

        var titleBlock = new TextBlock
        {
            Text = item.Title,
            FontSize = 14,
            FontWeight = item.IsRead ? FontWeights.Normal : FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = FluentTheme.Brush(_themeRoot, "TextFillColorPrimaryBrush"),
        };
        contentStack.Children.Add(titleBlock);

        contentStack.Children.Add(new TextBlock
        {
            Text = FormatDate(item.PublishAt),
            FontSize = 11,
            Foreground = FluentTheme.Brush(_themeRoot, "TextFillColorTertiaryBrush"),
        });

        Grid.SetColumn(contentStack, 1);
        card.Children.Add(contentStack);

        var listViewItem = new ListViewItem
        {
            Content = card,
            Tag = item,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
        };
        return listViewItem;
    }
}
