using System.Diagnostics;
using IDVBuff.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff;

public partial class App
{
    private async Task<bool> RequireUsageNoticeAsync()
    {
        if (UsageNotice.IsAccepted()) return true;

        var owner = window!;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = new Grid();
        owner.Content = root;
        var dialog = new ContentDialog
        {
            Title = UsageNotice.Title,
            Content = new ScrollViewer
            {
                MaxHeight = 460,
                Content = new TextBlock { Text = UsageNotice.Text, TextWrapping = TextWrapping.Wrap }
            },
            PrimaryButtonText = "请阅读（5 秒）",
            IsPrimaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.None
        };
        var elapsed = new Stopwatch();
        var accepted = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            var remaining = Math.Max(0, 5 - (int)elapsed.Elapsed.TotalSeconds);
            dialog.PrimaryButtonText = remaining > 0 ? $"请阅读（{remaining} 秒）" : UsageNotice.Confirmation;
            dialog.IsPrimaryButtonEnabled = remaining == 0;
        };
        dialog.Opened += (_, _) => { elapsed.Restart(); timer.Start(); };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (elapsed.Elapsed < TimeSpan.FromSeconds(5)) { args.Cancel = true; return; }
            try { UsageNotice.Accept(); accepted = true; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                args.Cancel = true;
                dialog.Title = "确认记录保存失败，请检查本地目录权限后重试";
                WriteStartupTrace("Usage notice acknowledgement could not be saved.", exception);
            }
        };
        // Escape, back navigation and programmatic dismissal never grant access.
        dialog.Closing += (_, args) => args.Cancel = !accepted;
        void OnClosed(object sender, WindowEventArgs args)
        {
            timer.Stop();
            completion.TrySetResult(false);
        }
        owner.Closed += OnClosed;
        root.Loaded += async (_, _) =>
        {
            try
            {
                dialog.XamlRoot = root.XamlRoot;
                await dialog.ShowAsync();
                completion.TrySetResult(accepted);
            }
            catch (Exception exception)
            {
                WriteStartupTrace("Usage notice presentation failed; startup denied.", exception);
                completion.TrySetResult(false);
            }
        };
        StartupSplash.Close();
        owner.Activate();
        try { return await completion.Task; }
        finally { timer.Stop(); owner.Closed -= OnClosed; }
    }
}
