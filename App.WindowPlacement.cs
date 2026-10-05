using IDVBuff.Lifecycle;
using WinRT.Interop;

namespace IDVBuff;

public partial class App
{
    private MainWindowPlacement? _lastMainWindowPlacement;

    private void RestoreMainWindowPlacement()
    {
        if (window is null) return;
        // The interaction guard blocks input; restoration must also avoid the
        // activating native show commands, which can activate a disabled HWND.
        var handle = WindowNative.GetWindowHandle(window);
        _lastMainWindowPlacement = MainWindowPlacement.Load();
        if (_lastMainWindowPlacement is { } placement
            && placement.Apply(handle, activate: false))
            return;

        // Preserve the first-launch default only when no usable saved placement exists.
        if (MainWindowPlacement.Capture(handle, false) is not { } initialPlacement
            || !(initialPlacement with { IsMaximized = true }).Apply(handle, activate: false))
            throw new InvalidOperationException("无法恢复主窗口，请重新启动 Identity Vision Bridge。");
    }

    private void CaptureMainWindowPlacement()
    {
        if (window is null || !mainWindowHasBeenShown) return;
        _lastMainWindowPlacement = MainWindowPlacement.Capture(
            WindowNative.GetWindowHandle(window),
            _lastMainWindowPlacement?.IsMaximized ?? false) ?? _lastMainWindowPlacement;
    }

    private void SaveMainWindowPlacement()
    {
        // Starting hidden and exiting without showing must not replace the previous session.
        if (!mainWindowHasBeenShown) return;
        CaptureMainWindowPlacement();
        _lastMainWindowPlacement?.Save();
    }
}
