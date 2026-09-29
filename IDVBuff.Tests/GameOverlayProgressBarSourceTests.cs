namespace IDVBuff.Tests;

public sealed class GameOverlayProgressBarSourceTests
{
    [Fact]
    public void ScanDisplayLayersDoNotActivateOrReleaseHeldInputs()
    {
        var root = FindRepositoryRoot();
        string Read(string name) => File.ReadAllText(Path.Combine(root, "Features", "Maps", name));
        foreach (var file in new[] { "GameOverlayProgressBar.cs", "MapOverlayNativeWindow.cs" })
        {
            var source = Read(file);
            Assert.DoesNotContain("ShowWindow(", source);
            Assert.Contains("SwpNoActivate | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpHideWindow", source);
        }
        foreach (var file in new[] { "GameOverlayProgressBar.cs", "MapOverlayNativeWindow.Rendering.Part2.cs" })
        {
            var source = Read(file);
            Assert.DoesNotContain("ShowWindow(", source);
            Assert.Contains("SwpNoActivate | SwpNoMove | SwpNoSize | SwpShowWindow", source);
        }
        Assert.DoesNotContain("ReleaseAllPressedInputs", Read("SessionOrchestrator.QuickScan.cs"));
    }

    [Fact]
    public void ScanFailureUsesRedHighContrastTerminalState()
    {
        var root = FindRepositoryRoot();
        var progress = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "GameOverlayProgressBar.cs"));
        var operations = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "SessionOrchestrator.QuickScan.cs"));

        Assert.Contains("public void Fail(string message)", progress);
        Assert.Contains("Color.FromArgb(255, 179, 38, 30)", progress);
        Assert.Contains("failed ? \"失败\"", progress);
        Assert.Contains("using var white = new SolidBrush(Color.White)", progress);
        Assert.Contains("if (scanCompleted)", operations);
        Assert.Contains("_scanProgressOverlay.Fail(", operations);
    }

    [Fact]
    public void ManualScanCannotBecomeObservationOrSucceedFromAnOpenMap()
    {
        var root = FindRepositoryRoot();
        string Read(string name) => File.ReadAllText(Path.Combine(root, "Features", "Maps", name));
        var quick = Read("SessionOrchestrator.QuickScan.cs");
        var pipeline = Read("SessionOrchestrator.Pipeline.Recognition.Part1.cs");
        var lifecycle = Read("SessionOrchestrator.MatchLifecycle.cs");
        Assert.DoesNotContain("StartMapObservation", quick);
        Assert.DoesNotContain("CreateObservationRecognitionState", pipeline);
        Assert.DoesNotContain("PublishMapObservation", pipeline);
        Assert.DoesNotContain("_hasCompletedQuickScanAlignment ||", quick);
        Assert.Contains("BackgroundScanStatus.CompletedIdentified", quick);
        Assert.Contains("_hasCompletedQuickScanAlignment = false;", lifecycle);
        Assert.Contains("ResolveCandidateSelectionAsync", pipeline);
    }

    [Fact]
    public void DisabledHeadingDoesNotStartUnconditionalCaptureOrReadSettingsEveryFrame()
    {
        var root = FindRepositoryRoot();
        var lifecycle = File.ReadAllText(Path.Combine(root, "Features", "Maps", "SessionOrchestrator.MatchLifecycle.cs"));
        var heading = File.ReadAllText(Path.Combine(root, "Features", "Maps", "SessionOrchestrator.NativeMiniMap.cs"));
        Assert.DoesNotContain("_captureSvc.PrepareViewportCapture()", lifecycle);
        Assert.Contains("Stopwatch.GetElapsedTime(preferencesChecked).TotalSeconds >= 1", heading);
        Assert.Contains("Task.Delay(1000, cancellationToken)", heading);
    }

    [Fact]
    public void ObservationDoesNotShowScanningStatus()
    {
        var path = Path.Combine(FindRepositoryRoot(), "Features", "Maps", "SessionOrchestrator.Observation.cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("ShowMapObservationStatus", source);
        Assert.DoesNotContain("_overlayStatus.Show", source);
        Assert.Contains("_observationNextAttemptAt - Environment.TickCount64", source);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
