using Microsoft.UI.Xaml;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private async void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new FeedbackDialog(XamlRoot);
            await dialog.ShowThemedAsync();
        }
        catch (Exception exception)
        {
            // 防御性捕获，防止多次点击或 WinUI 并发对话框限制引发未处理异常
            System.Diagnostics.Debug.WriteLine($"[Feedback] 打开反馈对话框失败: {exception.Message}");
        }
    }
}
