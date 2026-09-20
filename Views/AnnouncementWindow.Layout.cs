using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using IDVBuff.Features.Announcements;

namespace IDVBuff.Views;

public sealed partial class AnnouncementWindow
{
    private FrameworkElement BuildLayout()
    {
        // 最外层 Border：带有细边框与微圆角，绝对无任何原生或自制标题栏
        var rootBorder = new Border
        {
            Background = FluentTheme.Brush("LayerFillColorDefaultBrush"),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
        };

        // 纯粹左右分栏：直接顶到窗口最顶端，没有任何多余的标题栏行
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270, GridUnitType.Pixel) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rootBorder.Child = contentGrid;

        // -------------------- 左侧栏 --------------------
        var leftPanel = new Grid
        {
            Background = FluentTheme.Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        leftPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // 左侧分类切换胶囊
        var categoryBar = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(12, 14, 12, 10),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var categoryStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
        };
        categoryStack.Children.Add(CreateCategoryFilterButton("全部", null));
        categoryStack.Children.Add(CreateCategoryFilterButton("更新", AnnouncementCategories.Update));
        categoryStack.Children.Add(CreateCategoryFilterButton("通知", AnnouncementCategories.Notice));
        categoryStack.Children.Add(CreateCategoryFilterButton("冷知识", AnnouncementCategories.Tips));
        categoryBar.Children.Add(categoryStack);

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

        // 右侧 Header：分类徽章、标题、元信息与极简关闭按钮（安全拖拽区）
        var headerPanel = new Grid
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Padding = new Thickness(24, 14, 14, 12),
            BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 55, 55, 55)),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerPanel.PointerPressed += HeaderPanel_PointerPressed;
        headerPanel.PointerMoved += HeaderPanel_PointerMoved;
        headerPanel.PointerReleased += HeaderPanel_PointerReleased;
        headerPanel.PointerCaptureLost += (_, _) => _isDraggingWindow = false;

        var headerStack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(_categoryBadge);
        titleRow.Children.Add(_detailTitleBlock);
        headerStack.Children.Add(titleRow);
        headerStack.Children.Add(_detailMetaBlock);
        Grid.SetColumn(headerStack, 0);
        headerPanel.Children.Add(headerStack);

        // “不再自动提示此条通知”选框
        _dismissCheckBox.Margin = new Thickness(12, 0, 10, 0);
        Grid.SetColumn(_dismissCheckBox, 1);
        headerPanel.Children.Add(_dismissCheckBox);

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
        Grid.SetColumn(closeButton, 2);
        headerPanel.Children.Add(closeButton);

        Grid.SetRow(headerPanel, 0);
        rightPanel.Children.Add(headerPanel);

        // 右侧内容区：WebView2
        Grid.SetRow(_webView, 1);
        rightPanel.Children.Add(_webView);

        Grid.SetColumn(rightPanel, 1);
        contentGrid.Children.Add(rightPanel);

        rootBorder.ActualThemeChanged += (_, _) => UpdateWebViewTheme();

        return rootBorder;
    }

    private POINT _dragStartCursorPos;
    private Windows.Graphics.PointInt32 _dragStartWindowPos;
    private bool _isDraggingWindow;

    private void HeaderPanel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(sender as UIElement);
        if (pt.Properties.IsLeftButtonPressed)
        {
            if (GetCursorPos(out _dragStartCursorPos))
            {
                _dragStartWindowPos = _appWindow.Position;
                _isDraggingWindow = false;
                (sender as UIElement)?.CapturePointer(e.Pointer);
            }
        }
    }

    private void HeaderPanel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(sender as UIElement).Properties.IsLeftButtonPressed)
        {
            if (GetCursorPos(out var cur))
            {
                var dx = cur.X - _dragStartCursorPos.X;
                var dy = cur.Y - _dragStartCursorPos.Y;

                // 阈值保护：只有移动超过 5 像素才判定为拖拽，防止快速乱点误触发移动
                if (!_isDraggingWindow && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
                {
                    _isDraggingWindow = true;
                }

                if (_isDraggingWindow)
                {
                    _appWindow.Move(new Windows.Graphics.PointInt32(_dragStartWindowPos.X + dx, _dragStartWindowPos.Y + dy));
                }
            }
        }
    }

    private void HeaderPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingWindow = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
    }

    private Button CreateCategoryFilterButton(string label, string? category)
    {
        var btn = new Button
        {
            Content = label,
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        btn.Click += (_, _) =>
        {
            _currentCategory = category;
            ApplyFilter();
        };
        return btn;
    }

    private ListViewItem CreateAnnouncementListItem(AnnouncementItem item)
    {
        var card = new Grid { Padding = new Thickness(12, 8, 12, 8), ColumnSpacing = 8 };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 未读红点
        var unreadDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 239, 68, 68)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = item.IsRead ? Visibility.Collapsed : Visibility.Visible,
        };
        Grid.SetColumn(unreadDot, 0);
        card.Children.Add(unreadDot);

        var contentStack = new StackPanel { Spacing = 3 };

        var topRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        if (item.IsPinned)
        {
            topRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 234, 179, 8)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock { Text = "置顶", FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 234, 179, 8)) }
            });
        }
        topRow.Children.Add(new Border
        {
            Background = GetCategoryBrush(item.Category),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock { Text = AnnouncementCategories.GetDisplayName(item.Category), FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
        });
        if (!string.IsNullOrEmpty(item.Tag))
        {
            topRow.Children.Add(new TextBlock { Text = item.Tag, FontSize = 10, Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        }
        contentStack.Children.Add(topRow);

        var titleBlock = new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = item.IsRead ? FontWeights.Normal : FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = FluentTheme.Brush("TextFillColorPrimaryBrush"),
        };
        contentStack.Children.Add(titleBlock);

        var dateBlock = new TextBlock
        {
            Text = FormatDate(item.PublishAt),
            FontSize = 11,
            Foreground = FluentTheme.Brush("TextFillColorTertiaryBrush"),
        };
        contentStack.Children.Add(dateBlock);

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
