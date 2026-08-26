using IDVBuff.Features.Maps;
using IDVBuff.Lifecycle;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Dispatching;
using IDVBuff.Views;

namespace IDVBuff;

public partial class App
{
    private MapGlobalInputService? _safeModeInput;
    private static bool _isSafeMode = true;
    public static bool IsSafeMode
    {
        get => _isSafeMode;
        private set
        {
            _isSafeMode = value;
            Environment.SetEnvironmentVariable("IDVB_SAFE_MODE", value ? "1" : "0");
        }
    }

    private async Task<bool> TryCompleteSafeModeLaunchAsync(bool startMinimized)
    {
        if (!IsSafeMode)
            return false;

        WriteStartupTrace("Safe mode is active; CV, overlay and plugin runtimes will not be initialized.");
        if (GameProcessIntegrityService.Check().CurrentProcessIsElevated)
        {
            if (startMinimized)
                ShowMainWindow();
            await ShowSafeModeElevationWarningAsync();
        }

        await EnsureMapRuntimeDisabledAsync();
        await InitializeSafeModeTraditionalWindowInputAsync(
            DispatcherQueue.GetForCurrentThread());
        AutomaticUpdateLauncher.TryLaunch();
        if (startMinimized)
        {
            HideMainWindow();
            SetMainWindowCloaked(false);
        }
        return true;
    }

    private async Task InitializeSafeModeTraditionalWindowInputAsync(
        DispatcherQueue dispatcher)
    {
        var settings = await new MapRuntimeSettingsRepository().LoadAsync();
        _safeModeInput = new MapGlobalInputService(dispatcher);
        _safeModeInput.SwitchFloorInvoked += SafeModeSwitchFloorInvoked;
        ApplySafeModeTraditionalWindowBinding(settings.TraditionalWindowSwitchFloorBinding);
    }

    internal static void UpdateSafeModeTraditionalWindowBinding(MapInputBinding binding) =>
        _currentApp?.ApplySafeModeTraditionalWindowBinding(binding);

    private void ApplySafeModeTraditionalWindowBinding(MapInputBinding binding)
    {
        _safeModeInput?.ApplyBindings(
            new MapInputBinding(), new MapInputBinding(), new MapInputBinding(),
            new MapInputBinding(), new MapInputBinding(), binding,
            new MapInputBinding());
    }

    private static void SafeModeSwitchFloorInvoked(object? sender, MapInputInvokedEventArgs e) =>
        DirectMapDisplayWindow.SwitchFloorForOpenWindows();

    private void DisposeSafeModeTraditionalWindowInput()
    {
        if (_safeModeInput is null)
            return;
        _safeModeInput.SwitchFloorInvoked -= SafeModeSwitchFloorInvoked;
        _safeModeInput.Dispose();
        _safeModeInput = null;
    }

    private static async Task EnsureMapRuntimeDisabledAsync()
    {
        var repository = new MapRuntimeSettingsRepository();
        var settings = await repository.LoadAsync();
        if (!settings.IsEnabled)
            return;
        settings.IsEnabled = false;
        await repository.SaveAsync(settings);
    }

    private async Task ShowSafeModeElevationWarningAsync()
    {
        if (window?.Content is not FrameworkElement root || root.XamlRoot is null)
            return;

        await new ContentDialog
        {
            XamlRoot = root.XamlRoot,
            Title = "安全模式不需要管理员权限",
            Content = "当前 Identity Vision Bridge 正以管理员权限运行。安全模式不需要管理员权限，建议退出后以普通用户权限重新启动；你也可以关闭此提示并继续使用。",
            CloseButtonText = "继续使用",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }
}
