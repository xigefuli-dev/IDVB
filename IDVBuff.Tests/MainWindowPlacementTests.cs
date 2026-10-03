using System.Runtime.InteropServices;
using IDVBuff.Lifecycle;
using Xunit;

namespace IDVBuff.Tests;

public sealed class MainWindowPlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SavedPlacementSurvivesReloadAndKeepsNormalBounds(bool maximized)
    {
        var path = Path.Combine(AppDataPaths.RootDirectory, "placement-tests", Guid.NewGuid() + ".json");
        var saved = new MainWindowPlacement(-1100, 120, -300, 720, maximized);
        saved.Save(path);
        Assert.Equal(saved, MainWindowPlacement.Load(path));
    }

    [Fact]
    public void MissingCorruptAndInvalidSavedBoundsUseStartupDefault()
    {
        var path = Path.Combine(AppDataPaths.RootDirectory, "placement-tests", Guid.NewGuid() + ".json");
        Assert.Null(MainWindowPlacement.Load(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "broken json");
        Assert.Null(MainWindowPlacement.Load(path));
        File.WriteAllText(path, "{\"Left\":800,\"Top\":120,\"Right\":300,\"Bottom\":720,\"IsMaximized\":true}");
        Assert.Null(MainWindowPlacement.Load(path));
        Assert.False(new MainWindowPlacement(int.MinValue, 0, int.MaxValue, 720, false).IsValid);
    }

    [Fact]
    public void NativePlacementRoundTripRestoresMovedResizedWindow()
    {
        WithWindow(handle =>
        {
            var saved = MainWindowPlacement.Capture(handle, false);
            Assert.NotNull(saved);
            Assert.True(new MainWindowPlacement(160, 170, 860, 670, false).Apply(handle));
            Assert.NotEqual(saved, MainWindowPlacement.Capture(handle, false));
            Assert.True(saved.Apply(handle));
            Assert.Equal(saved, MainWindowPlacement.Capture(handle, false));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MinimizeAndHidePreserveLastVisibleStateAndNormalSize(bool maximized)
    {
        WithWindow(handle =>
        {
            var original = MainWindowPlacement.Capture(handle, false)!;
            Assert.True((original with { IsMaximized = maximized }).Apply(handle));
            var visible = MainWindowPlacement.Capture(handle, false);
            Assert.Equal(original with { IsMaximized = maximized }, visible);
            // The splash handoff and tray activation both show the same HWND again.
            ShowWindow(handle, 8); // SW_SHOWNA preserves the saved presentation.
            Assert.Equal(visible, MainWindowPlacement.Capture(handle, false));
            ShowWindow(handle, 5); // SW_SHOW
            Assert.Equal(visible, MainWindowPlacement.Capture(handle, false));
            ShowWindow(handle, 6); // SW_MINIMIZE
            Assert.Equal(visible, MainWindowPlacement.Capture(handle, maximized));
            ShowWindow(handle, 0); // SW_HIDE (tray)
            Assert.Equal(visible, MainWindowPlacement.Capture(handle, maximized));
            Assert.True(visible!.Apply(handle));
            Assert.Equal(visible, MainWindowPlacement.Capture(handle, false));
            if (maximized)
            {
                ShowWindow(handle, 9); // SW_RESTORE: keep the normal rectangle after maximizing.
                Assert.Equal(original, MainWindowPlacement.Capture(handle, false));
            }
        });
    }

    [Fact]
    public void RemovedMonitorPlacementIsRecoveredOnScreen()
    {
        WithWindow(handle =>
        {
            var offScreen = new MainWindowPlacement(100000, 100000, 100800, 100600, false);
            Assert.True(offScreen.Apply(handle));
            var recovered = MainWindowPlacement.Capture(handle, false);
            Assert.NotNull(recovered);
            Assert.NotEqual(offScreen, recovered);
            Assert.Equal(800, recovered.Right - recovered.Left);
            Assert.Equal(600, recovered.Bottom - recovered.Top);
        });
    }

    private static void WithWindow(Action<IntPtr> verify)
    {
        // A separate Win32 test window exercises actual WINDOWPLACEMENT behavior
        // without moving the user's IDVB window or touching installed preferences.
        var handle = CreateWindowEx(0, "STATIC", "IDVB placement regression", 0x00CF0000,
            100, 100, 800, 600, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, handle);
        try { verify(handle); }
        finally { DestroyWindow(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr handle);
}
