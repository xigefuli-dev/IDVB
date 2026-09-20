using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using IDVBuff.Features.Notifications;

namespace IDVBuff.Views;

public sealed partial class MainSettingsPage
{
    private Border CreateOverlayNotificationTestCard()
    {
        var layout = new Grid { MinHeight = 86, Padding = new Thickness(26, 15, 24, 15) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = "屏幕通知测试",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock
        {
            Text = "在一栏内触发三类通知，测试红色（错误）、橙色（警告）、绿色（通知）三种配色、换行高度（要点A）、蓝色进度条与队列平滑位移（默认5秒自动消失）",
            FontSize = 14,
            Foreground = FluentTheme.Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(labels);

        // 一栏内包含三个按钮，每个按钮使用对应的颜色
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 1. 错误按钮（红色系）
        var errorButton = new Button
        {
            Content = "错误 (红)",
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 170, 170)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        errorButton.Click += (_, _) =>
        {
            try { DeveloperNotificationTrigger.TriggerError(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainSettings] 错误通知测试触发异常: {ex}"); }
        };
        buttonsPanel.Children.Add(errorButton);

        // 2. 警告按钮（橙色系）
        var warningButton = new Button
        {
            Content = "警告 (橙)",
            Background = new SolidColorBrush(Color.FromArgb(255, 217, 119, 6)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 224, 130)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        warningButton.Click += (_, _) =>
        {
            try { DeveloperNotificationTrigger.TriggerWarning(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainSettings] 警告通知测试触发异常: {ex}"); }
        };
        buttonsPanel.Children.Add(warningButton);

        // 3. 通知按钮（绿色系）
        var noticeButton = new Button
        {
            Content = "通知 (绿)",
            Background = new SolidColorBrush(Color.FromArgb(255, 16, 124, 65)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 134, 239, 172)),
            BorderThickness = new Thickness(1.2),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 7, 14, 7),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        noticeButton.Click += (_, _) =>
        {
            try { DeveloperNotificationTrigger.TriggerNotice(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainSettings] 普通通知测试触发异常: {ex}"); }
        };
        buttonsPanel.Children.Add(noticeButton);

        Grid.SetColumn(buttonsPanel, 1);
        layout.Children.Add(buttonsPanel);

        return new Border
        {
            Background = FluentTheme.CardBrush(),
            BorderBrush = FluentTheme.Brush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = layout
        };
    }
}
