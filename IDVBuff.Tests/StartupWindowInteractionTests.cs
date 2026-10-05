using System.Runtime.InteropServices;
using IDVBuff.Lifecycle;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "9")]
public sealed class StartupWindowInteractionTests
{
    private readonly ITestOutputHelper _output;

    public StartupWindowInteractionTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoringNormalAndMaximizedPlacementDoesNotActivateGuardedWindow(bool maximized)
    {
        WithWindow(handle =>
        {
            var active = GetActiveWindow();
            var foreground = GetForegroundWindow();
            using (new StartupWindowInteractionGuard(handle))
            {
                Assert.False(IsWindowEnabled(handle));
                Assert.NotEqual(0, GetWindowLong(handle, -20) & 0x08000000);
                var placement = new MainWindowPlacement(130, 140, 930, 740, maximized);

                RecordWindowState("guarded-before-apply", handle);
                Assert.True(placement.Apply(handle, activate: false));
                var afterApply = RecordWindowState("after-apply-before-show", handle);
                ShowWindow(handle, 8); // SW_SHOWNA, also used when priming WinUI Loaded.
                var afterShow = RecordWindowState("after-showNA", handle);

                Assert.Equal(active, afterApply.Active);
                Assert.Equal(foreground, afterApply.Foreground);
                Assert.Equal(active, afterShow.Active);
                Assert.Equal(foreground, afterShow.Foreground);
                Assert.False(IsWindowEnabled(handle));
                Assert.NotEqual(0, GetWindowLong(handle, -20) & 0x08000000);
                Assert.Equal(maximized, IsZoomed(handle));
                Assert.True(IsWindowVisible(handle));
            }
            Assert.True(IsWindowEnabled(handle));
            Assert.Equal(0, GetWindowLong(handle, -20) & 0x08000000);
        });
    }

    private (IntPtr Active, IntPtr Foreground) RecordWindowState(string stage, IntPtr handle)
    {
        var active = GetActiveWindow();
        var foreground = GetForegroundWindow();
        _output.WriteLine($"{stage}: hwnd=0x{handle.ToInt64():X}; active=0x{active.ToInt64():X}; "
            + $"foreground=0x{foreground.ToInt64():X}; enabled={IsWindowEnabled(handle)}; "
            + $"style=0x{GetWindowLong(handle, -16):X8}; exstyle=0x{GetWindowLong(handle, -20):X8}; "
            + $"placement={MainWindowPlacement.Capture(handle, false)}");
        return (active, foreground);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NonActivatingRestoreKeepsNormalBoundsWhenPreviouslyMinimized(
        bool wasMaximized, bool restoreMaximized)
    {
        WithWindow(handle =>
        {
            using var guard = new StartupWindowInteractionGuard(handle);
            var active = GetActiveWindow();
            var foreground = GetForegroundWindow();
            Assert.True(new MainWindowPlacement(130, 140, 930, 740, wasMaximized)
                .Apply(handle, activate: false));
            ShowWindow(handle, 7); // SW_SHOWMINNOACTIVE
            var requested = new MainWindowPlacement(160, 170, 960, 770, restoreMaximized);

            Assert.True(requested.Apply(handle, activate: false));
            var restored = RecordWindowState("restored-from-minimized", handle);

            Assert.Equal(active, restored.Active);
            Assert.Equal(foreground, restored.Foreground);
            Assert.Equal(requested, MainWindowPlacement.Capture(handle, false));
            Assert.Equal(restoreMaximized, IsZoomed(handle));
            Assert.False(IsWindowEnabled(handle));
            if (restoreMaximized)
            {
                ShowWindow(handle, 4); // Restore the saved normal rectangle without activation.
                Assert.Equal(requested with { IsMaximized = false }, MainWindowPlacement.Capture(handle, false));
                Assert.Equal(active, GetActiveWindow());
                Assert.Equal(foreground, GetForegroundWindow());
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonActivatingRestoreRecoversRemovedMonitorBounds(bool maximized)
    {
        WithWindow(handle =>
        {
            using var guard = new StartupWindowInteractionGuard(handle);
            var active = GetActiveWindow();
            var foreground = GetForegroundWindow();
            var requested = new MainWindowPlacement(100000, 100000, 100800, 100600, maximized);

            Assert.True(requested.Apply(handle, activate: false));
            var recovered = MainWindowPlacement.Capture(handle, false);

            Assert.NotNull(recovered);
            Assert.NotEqual(requested, recovered);
            Assert.Equal(800, recovered.Right - recovered.Left);
            Assert.Equal(600, recovered.Bottom - recovered.Top);
            Assert.Equal(maximized, recovered.IsMaximized);
            Assert.Equal(active, GetActiveWindow());
            Assert.Equal(foreground, GetForegroundWindow());
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReleasingGuardRestoresOriginalInteractionStateAndPreservesLaterStyleChanges(
        bool wasEnabled, bool hadNoActivate)
    {
        WithWindow(handle =>
        {
            EnableWindow(handle, wasEnabled);
            var originalStyle = GetWindowLong(handle, -20) | 0x00000080; // TOOLWINDOW
            SetWindowLong(handle, -20, hadNoActivate
                ? originalStyle | 0x08000000 : originalStyle & ~0x08000000);
            var guard = new StartupWindowInteractionGuard(handle);
            Assert.False(IsWindowEnabled(handle));
            Assert.NotEqual(0, GetWindowLong(handle, -20) & 0x08000000);
            // Other window owners can remove and add unrelated flags during startup.
            SetWindowLong(handle, -20, (GetWindowLong(handle, -20) & ~0x00000080) | 0x00040000);
            guard.Dispose();

            Assert.Equal(wasEnabled, IsWindowEnabled(handle));
            Assert.Equal(hadNoActivate, (GetWindowLong(handle, -20) & 0x08000000) != 0);
            Assert.Equal(0, GetWindowLong(handle, -20) & 0x00000080);
            Assert.NotEqual(0, GetWindowLong(handle, -20) & 0x00040000); // APPWINDOW
            // Repeated cleanup must not overwrite a later owner's decision.
            EnableWindow(handle, !wasEnabled);
            var afterRelease = GetWindowLong(handle, -20) ^ 0x08000000;
            SetWindowLong(handle, -20, afterRelease);
            guard.Dispose();
            Assert.Equal(!wasEnabled, IsWindowEnabled(handle));
            Assert.Equal(afterRelease, GetWindowLong(handle, -20));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstPresentationWithoutSavedPlacementKeepsForegroundAndRequestedMinimization(bool minimized)
    {
        WithWindow(handle =>
        {
            Assert.False(IsWindowVisible(handle));
            var active = GetActiveWindow();
            var foreground = GetForegroundWindow();
            var bounds = MainWindowPlacement.Capture(handle, false);
            using (new StartupWindowInteractionGuard(handle))
            {
                ShowWindow(handle, minimized ? 7 : 8);
                Assert.True(IsWindowVisible(handle));
                Assert.Equal(minimized, IsIconic(handle));
                Assert.False(IsWindowEnabled(handle));
                Assert.Equal(active, GetActiveWindow());
                Assert.Equal(foreground, GetForegroundWindow());
                Assert.Equal(bounds, MainWindowPlacement.Capture(handle, false));
            }
            Assert.True(IsWindowEnabled(handle));
            Assert.Equal(minimized, IsIconic(handle));
            Assert.Equal(active, GetActiveWindow());
            Assert.Equal(foreground, GetForegroundWindow());
        });
    }

    [Fact]
    public void FailedStartupScopeRestoresInteractionWithoutActivatingItsWindow()
    {
        WithWindow(handle =>
        {
            var originalStyle = GetWindowLong(handle, -20);
            var active = GetActiveWindow();
            var foreground = GetForegroundWindow();
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var guard = new StartupWindowInteractionGuard(handle);
                ShowWindow(handle, 8);
                throw new InvalidOperationException("Startup initialization failed.");
            }));
            Assert.True(IsWindowEnabled(handle));
            Assert.Equal(originalStyle, GetWindowLong(handle, -20));
            Assert.Equal(active, GetActiveWindow());
            Assert.Equal(foreground, GetForegroundWindow());
        });
    }

    [Fact]
    public void DestroyedStartupWindowCanBeReleasedAndCannotBeGuardedAgain()
    {
        WithWindow(handle =>
        {
            var guard = new StartupWindowInteractionGuard(handle);
            Assert.True(DestroyWindow(handle));
            Assert.False(IsWindow(handle));
            guard.Dispose();
            guard.Dispose();
            Assert.Throws<ArgumentException>(() => new StartupWindowInteractionGuard(handle));
        });
        Assert.Throws<ArgumentException>(() => new StartupWindowInteractionGuard(IntPtr.Zero));
    }

    [Theory]
    [InlineData(10, 100, true)]
    [InlineData(20, 100, false)] // The foreground changed without an input timestamp change.
    [InlineData(10, 101, false)] // Input to the original app must also preserve user focus.
    [InlineData(20, 101, false)]
    [InlineData(30, 101, true)] // The user has already explicitly activated the ready main window.
    public void StartupHandoffHonorsTheCurrentForegroundAndUserInput(
        long foreground, uint input, bool mayActivate)
    {
        var launch = new StartupFocusSnapshot(new IntPtr(10), 100);
        var current = new StartupFocusSnapshot(new IntPtr(foreground), input);

        Assert.Equal(mayActivate, launch.MayActivateMainWindow(current, new IntPtr(30)));
    }

    [Fact]
    public void UnavailableInputOrWindowInformationDoesNotGrantActivation()
    {
        Assert.False(new StartupFocusSnapshot(new IntPtr(10), null)
            .MayActivateMainWindow(new StartupFocusSnapshot(new IntPtr(10), null), new IntPtr(30)));
        Assert.False(new StartupFocusSnapshot(IntPtr.Zero, 100)
            .MayActivateMainWindow(new StartupFocusSnapshot(IntPtr.Zero, 100), new IntPtr(30)));
        Assert.False(new StartupFocusSnapshot(new IntPtr(10), 100)
            .MayActivateMainWindow(new StartupFocusSnapshot(new IntPtr(10), 100), IntPtr.Zero));
    }

    [Fact]
    public void SwitchingAwayAndBackDuringStartupDoesNotEraseTheUsersNewInput()
    {
        var launch = new StartupFocusSnapshot(new IntPtr(10), 100);
        var main = new IntPtr(30);

        Assert.False(launch.MayActivateMainWindow(new StartupFocusSnapshot(new IntPtr(20), 101), main));
        Assert.False(launch.MayActivateMainWindow(new StartupFocusSnapshot(new IntPtr(10), 102), main));
        Assert.True(launch.MayActivateMainWindow(new StartupFocusSnapshot(main, 103), main));
        Assert.False(launch.MayActivateMainWindow(new StartupFocusSnapshot(main, 103), IntPtr.Zero));
    }

    [Theory]
    [InlineData(100u, null, false)]
    [InlineData(null, 100u, false)]
    [InlineData(uint.MaxValue, 0u, false)] // GetLastInputInfo's timestamp can wrap.
    [InlineData(0u, 0u, true)]
    public void HandoffRequiresBothInputSamplesAndTreatsTimestampWrapAsNewInput(
        uint? launchInput, uint? currentInput, bool mayActivate)
    {
        var launch = new StartupFocusSnapshot(new IntPtr(10), launchInput);
        var current = new StartupFocusSnapshot(new IntPtr(10), currentInput);

        Assert.Equal(mayActivate, launch.MayActivateMainWindow(current, new IntPtr(30)));
    }

    [Fact]
    public void ExplicitlyActiveMainWindowDoesNotDependOnThePreviousApplicationsInputSample()
    {
        var main = new IntPtr(30);
        var unknownLaunch = new StartupFocusSnapshot(IntPtr.Zero, null);

        Assert.True(unknownLaunch.MayActivateMainWindow(new StartupFocusSnapshot(main, null), main));
        Assert.False(unknownLaunch.MayActivateMainWindow(new StartupFocusSnapshot(IntPtr.Zero, null), main));
    }

    private static void WithWindow(Action<IntPtr> verify)
    {
        var handle = CreateWindowEx(0, "STATIC", "IDVB startup interaction regression", 0x00CF0000,
            100, 100, 800, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, handle);
        try { verify(handle); }
        finally { DestroyWindow(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr handle, bool enabled);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);
}
