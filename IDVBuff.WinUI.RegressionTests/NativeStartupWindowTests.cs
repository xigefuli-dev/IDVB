using System.Runtime.InteropServices;
using IDVBuff.Lifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Xunit;

namespace IDVBuff.WinUI.RegressionTests;

internal static class NativeStartupWindowTests
{
    public static IReadOnlyList<NativeRegressionCase> Cases { get; } =
    [
        new("Startup/NormalPlacementDoesNotActivateWinUI", fixture => Verify(false)),
        new("Startup/MaximizedPlacementDoesNotActivateWinUI", fixture => Verify(true)),
        new("Startup/MinimizedNormalToNormal", fixture => Verify(false, minimized: true)),
        new("Startup/MinimizedNormalToMaximized", fixture => Verify(true, minimized: true)),
        new("Startup/MinimizedMaximizedToNormal", fixture => Verify(false, sourceMax: true, minimized: true)),
        new("Startup/MinimizedMaximizedToMaximized", fixture => Verify(true, sourceMax: true, minimized: true)),
        new("Startup/RemovedMonitorNormalBounds", fixture => Verify(false, offscreen: true)),
        new("Startup/RemovedMonitorMaximizedBounds", fixture => Verify(true, offscreen: true)),
        new("Startup/FirstLaunchCapturedBoundsMaximize", fixture => Verify(true, firstLaunch: true)),
        new("Startup/NativeNonActivatingNormalStage", fixture => Verify(false, nativeStages: true)),
        new("Startup/NativeNonActivatingMaximizedStages", fixture => Verify(true, nativeStages: true)),
        new("Startup/StartMinimizedRemainsHiddenAfterGuardRelease", fixture => Verify(true, startHidden: true))
    ];

    private static Task Verify(bool maximized, bool sourceMax = false, bool minimized = false,
        bool offscreen = false, bool firstLaunch = false, bool nativeStages = false, bool startHidden = false)
    {
        // Do not activate a witness or restore a user's foreground afterward.
        // A thread-active HWND of zero is a valid starting state.
        var active = GetActiveWindow();
        var foreground = GetForegroundWindow();
        var target = new Window { Title = "Identity Vision Bridge startup regression", Content = new Grid() };
        var handle = WindowNative.GetWindowHandle(target);
        var observer = new ActivationObserver(handle, active, foreground);
        var initialEnabled = IsWindowEnabled(handle);
        var initialNoActivate = GetWindowLong(handle, -20) & 0x08000000;
        var cloaked = 1;
        var startupPending = true;
        void AppWindowChanged(AppWindow window, AppWindowChangedEventArgs change)
        {
            // Mirrors the production startup-pending boundary around minimize-to-tray.
            // Restore's temporary minimized state must not hide the incomplete window.
            if (!startupPending && change.DidPresenterChange
                && window.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) window.Hide();
        }
        target.AppWindow.Changed += AppWindowChanged;
        try
        {
            Assert.Equal(0, DwmSetWindowAttribute(handle, 13, ref cloaked, sizeof(int)));
            observer.Install();
            using (new StartupWindowInteractionGuard(handle))
            {
                observer.Checkpoint("guard-before-restore");
                if (minimized)
                {
                    Assert.True(new MainWindowPlacement(130, 140, 930, 740, sourceMax).Apply(handle, activate: false));
                    observer.Checkpoint("source-placement");
                    ShowWindow(handle, 7);
                    observer.Checkpoint("source-minimized");
                    Assert.Equal(OverlappedPresenterState.Minimized, ((OverlappedPresenter)target.AppWindow.Presenter).State);
                }
                var left = offscreen ? 100000 : 160;
                var top = offscreen ? 100000 : 170;
                var requested = firstLaunch
                    ? Assert.IsType<MainWindowPlacement>(MainWindowPlacement.Capture(handle, false)) with { IsMaximized = true }
                    : new MainWindowPlacement(left, top, left + 800, top + 600, maximized);
                if (nativeStages) ApplyDocumentedNativeStages(handle, requested, observer);
                else Assert.True(requested.Apply(handle, activate: false), "Production MainWindowPlacement.Apply(false) failed.");
                observer.Checkpoint("production-restore-completed");
                var restored = Assert.IsType<MainWindowPlacement>(MainWindowPlacement.Capture(handle, false));
                Assert.Equal(maximized, restored.IsMaximized);
                Assert.Equal(requested.Right - requested.Left, restored.Right - restored.Left);
                Assert.Equal(requested.Bottom - requested.Top, restored.Bottom - restored.Top);
                if (!offscreen) Assert.Equal(requested, restored);
                else Assert.NotEqual(requested, restored);
                Assert.Equal(maximized ? OverlappedPresenterState.Maximized : OverlappedPresenterState.Restored,
                    ((OverlappedPresenter)target.AppWindow.Presenter).State);
                target.AppWindow.Show(false);
                observer.Checkpoint("AppWindow.Show(false)");
                if (startHidden)
                {
                    target.AppWindow.Hide();
                    observer.Checkpoint("start-minimized-hide");
                    Assert.False(IsWindowVisible(handle));
                }
                else
                {
                    cloaked = 0;
                    Assert.Equal(0, DwmSetWindowAttribute(handle, 13, ref cloaked, sizeof(int)));
                    ShowWindow(handle, 8);
                    observer.Checkpoint("uncloak-ShowNA");
                    Assert.Equal(restored, MainWindowPlacement.Capture(handle, false));
                    if (maximized)
                    {
                        ShowWindow(handle, 4);
                        observer.Checkpoint("normal-restore-after-maximize");
                        Assert.Equal(restored with { IsMaximized = false }, MainWindowPlacement.Capture(handle, false));
                    }
                }
            }
            startupPending = false;
            observer.Checkpoint("guard-released", guarded: false);
            Assert.Equal(initialEnabled, IsWindowEnabled(handle));
            Assert.Equal(initialNoActivate, GetWindowLong(handle, -20) & 0x08000000);
            if (startHidden) Assert.False(IsWindowVisible(handle));
            observer.AssertNoTransientActivation();
        }
        finally
        {
            target.AppWindow.Changed -= AppWindowChanged;
            observer.Dispose();
            target.Close();
        }
        return Task.CompletedTask;
    }

    // Independent native checkpoints explain why SW_SHOWMAXIMIZED cannot be used
    // at startup. Production Apply(false) is exercised separately above.
    private static void ApplyDocumentedNativeStages(IntPtr handle, MainWindowPlacement requested, ActivationObserver observer)
    {
        var placement = new Placement
        {
            Length = Marshal.SizeOf<Placement>(),
            ShowCommand = requested.IsMaximized ? 7 : 4,
            MinPosition = new Point { X = -1, Y = -1 },
            MaxPosition = new Point { X = -1, Y = -1 },
            NormalPosition = new Rect { Left = requested.Left, Top = requested.Top, Right = requested.Right, Bottom = requested.Bottom }
        };
        Assert.True(SetWindowPlacement(handle, ref placement));
        observer.Checkpoint("stage1-SHOWMINNOACTIVE-or-SHOWNOACTIVATE");
        if (!requested.IsMaximized) return;
        placement.ShowCommand = 2;
        placement.Flags = 2;
        Assert.True(SetWindowPlacement(handle, ref placement));
        observer.Checkpoint("stage2-already-minimized-restore-to-maximized");
        ShowWindow(handle, 4);
        observer.Checkpoint("stage3-SHOWNOACTIVATE");
    }

    private sealed class ActivationObserver : IDisposable
    {
        private const nuint SubclassId = 0x49445654;
        private readonly IntPtr _handle;
        private readonly IntPtr _active;
        private readonly IntPtr _foreground;
        private readonly SubclassProcedure _procedure;
        private readonly List<string> _activation = [];
        private bool _installed;

        public ActivationObserver(IntPtr handle, IntPtr active, IntPtr foreground)
        {
            _handle = handle;
            _active = active;
            _foreground = foreground;
            _procedure = Observe;
        }

        public void Install()
        {
            Assert.True(SetWindowSubclass(_handle, _procedure, SubclassId, 0));
            _installed = true;
        }

        public void Checkpoint(string stage, bool guarded = true)
        {
            var placement = new Placement { Length = Marshal.SizeOf<Placement>() };
            Assert.True(GetWindowPlacement(_handle, ref placement));
            Assert.True(GetWindowRect(_handle, out var rect));
            var detail = $"{stage}; active=0x{GetActiveWindow().ToInt64():X}; foreground=0x{GetForegroundWindow().ToInt64():X}; "
                + $"enabled={IsWindowEnabled(_handle)}; exstyle=0x{GetWindowLong(_handle, -20):X8}; "
                + $"showCmd={placement.ShowCommand}; flags={placement.Flags}; normal={placement.NormalPosition}; rect={rect}";
            Assert.True(_active == GetActiveWindow(), detail);
            Assert.True(_foreground == GetForegroundWindow(), detail);
            if (guarded)
            {
                Assert.False(IsWindowEnabled(_handle), detail);
                Assert.NotEqual(0, GetWindowLong(_handle, -20) & 0x08000000);
            }
            AssertNoTransientActivation();
        }

        public void AssertNoTransientActivation() => Assert.True(_activation.Count == 0, string.Join(Environment.NewLine, _activation));

        private IntPtr Observe(IntPtr window, uint message, nuint wParam, IntPtr lParam, nuint id, nuint data)
        {
            // Never throw an assertion through an unmanaged window-procedure frame.
            if ((message == 0x0006 && (wParam & 0xFFFF) != 0) || message == 0x0007)
                _activation.Add($"transient message=0x{message:X}; wParam={wParam}; active=0x{GetActiveWindow().ToInt64():X}");
            return DefSubclassProc(window, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (_installed) RemoveWindowSubclass(_handle, _procedure, SubclassId);
            GC.KeepAlive(_procedure);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public override readonly string ToString() => $"({Left},{Top},{Right},{Bottom})";
    }
    [StructLayout(LayoutKind.Sequential)] private struct Placement
    {
        public int Length, Flags, ShowCommand;
        public Point MinPosition, MaxPosition;
        public Rect NormalPosition;
    }
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, nuint wParam, IntPtr lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, nuint wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr window, ref Placement placement);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
