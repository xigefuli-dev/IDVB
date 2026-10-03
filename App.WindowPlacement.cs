using IDVBuff.Lifecycle;
using Microsoft.UI.Windowing;
using WinRT.Interop;

namespace IDVBuff;

public partial class App
{
    private MainWindowPlacement? _lastMainWindowPlacement;

    private void RestoreMainWindowPlacement()
    {
        if (window is null) return;
        _lastMainWindowPlacement = MainWindowPlacement.Load();
        if (_lastMainWindowPlacement is { } placement
            && placement.Apply(WindowNative.GetWindowHandle(window)))
            return;

        // Preserve the first-launch default only when no usable saved placement exists.
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
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
