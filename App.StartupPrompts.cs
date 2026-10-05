using IDVBuff.Features.QuickStart;
using IDVBuff.Lifecycle;
using IDVBuff.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff;

public partial class App
{
    private async Task ShowUpdatedSuccessfullyAsync()
    {
        var cancellationToken = _startupPresentationCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        UpdateLifecycleState.WasRestartedAfterUpdate = false;
        if (window?.Content is not FrameworkElement root)
            return;
        await new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "更新完成",
            Content = $"Identity Vision Bridge 已更新到 {BuildVersionInfo.BuildVersion}。",
            CloseButtonText = "知道了"
        }.ShowThemedAsync(cancellationToken: cancellationToken);
    }

    private async Task ShowQuickStartAsync(Features.Maps.SessionOrchestrator session)
    {
        var cancellationToken = _startupPresentationCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var stateStore = new QuickStartStateStore();
        if (!stateStore.ShouldShow)
            return;

        FrameworkElement? root = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            root = window?.Content as FrameworkElement;
            if (root?.XamlRoot is not null)
                break;
            await Task.Delay(100, cancellationToken);
        }

        var choice = await QuickStartDialog.ShowAsync(root?.XamlRoot, cancellationToken);
        if (choice is null || IsApplicationStopping)
            return;

        if (choice == QuickStartChoice.UseRecommendedSettings)
        {
            try
            {
                await ApplyQuickStartSelectionAsync(session);
                if (IsApplicationStopping) return;
                if (_mainFrame?.Content is MainPage mainPage)
                    await mainPage.ShowRecommendedConfigurationGuideAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (IsApplicationStopping)
            {
                return;
            }
            catch (Exception exception)
            {
                WriteStartupTrace("Unable to apply quick-start recommended settings.", exception);
                return;
            }
        }

        if (IsApplicationStopping) return;
        try
        {
            stateStore.MarkCompleted();
        }
        catch (Exception exception)
        {
            // A marker failure must not prevent the application from starting.
            WriteStartupTrace("Unable to persist quick-start completion.", exception);
        }
    }

    private void Runtime_ElevationRequiredDetected(object? sender, EventArgs e)
    {
        // The integrity check runs during SessionOrchestrator initialization.
        // Defer the mandatory dialog until the rest of OnLaunched has completed.
        startupElevationRequired = true;
        WriteStartupTrace("Startup requires administrator privileges.");
    }

    private async Task ShowStartupElevationRequiredAsync()
    {
        var currentWindow = window;
        var cancellationToken = _startupPresentationCancellation.Token;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (currentWindow is null)
                return;

            FrameworkElement? root = null;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                root = currentWindow.Content as FrameworkElement;
                if (root?.XamlRoot is not null)
                    break;
                await Task.Delay(150, cancellationToken);
            }

            if (root?.XamlRoot is not null)
            {
                await new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = "需要管理员权限",
                    Content = "Identity Vision Bridge 必须以管理员权限运行，请退出后重新以管理员权限打开。",
                    CloseButtonText = "退出",
                    DefaultButton = ContentDialogButton.Close
                }.ShowThemedAsync(cancellationToken: cancellationToken);
            }
        }
        catch (OperationCanceledException) when (IsApplicationStopping)
        {
        }
        catch (Exception exception)
        {
            WriteStartupTrace("Unable to show the administrator privilege prompt.", exception);
        }
        finally
        {
            RequestApplicationExit();
        }
    }

}
