using IDVBuff.Lifecycle;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace IDVBuff;

public partial class App
{
    private readonly SponsorshipReminder _sponsorshipReminder = new(
        ApplicationUsageTracker.Current.PreviousUsage,
        Path.Combine(AppDataPaths.RootDirectory, "sponsorship-reminder-v1.marker"));

    private async Task TryShowSponsorshipReminderAsync(Features.Maps.SessionOrchestrator session)
    {
        var token = _startupPresentationCancellation.Token;
        bool Suitable() => !token.IsCancellationRequested && !IsApplicationStopping
            && !_accessStopping && !startupElevationRequired
            && window is { } mainWindow && mainWindow.Content is FrameworkElement root
            && root.XamlRoot is not null
            && new SponsorshipStartupContext(
                _mainFrame?.Content is MainPage { IsSponsorshipHome: true },
                StartupFocusSnapshot.Capture().ForegroundWindow == WindowNative.GetWindowHandle(mainWindow),
                root.XamlRoot.IsHostVisible, session.IsMatchStarted, session.IsGameMapOpen,
                VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).Count != 0,
                token.IsCancellationRequested || IsApplicationStopping).Suitable;

        try
        {
            // Exactly one opportunity after startup presentation, with no navigation/focus retries.
            if (!_sponsorshipReminder.TryReserve(Suitable(), token)) return;
            if (!Suitable()) return;
            var page = (MainPage)_mainFrame!.Content;
            var dialog = new ContentDialog
            {
                XamlRoot = page.XamlRoot, Title = "感谢你一直使用 IDVB",
                Content = "你已经累计使用 IDVB 超过 24 小时。如果它帮到了你，欢迎通过赞助支持后续开发与维护。是否赞助都不影响正常使用。",
                PrimaryButtonText = "支持开发", CloseButtonText = "继续使用",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowThemedAsync(cancellationToken: token) == ContentDialogResult.Primary
                && !IsApplicationStopping) page.OpenSponsorshipFromHome();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { WriteStartupTrace("Optional sponsorship prompt suppressed.", exception); }
    }
}
