using IDVBuff.Diagnostics;
using IDVBuff.Lifecycle;
using Xunit;

namespace IDVBuff.Tests;

public sealed class RealtimePerformanceTrackerTests
{
    [Fact]
    public void CaptureSnapshot_ReturnsValidMemoryMetrics()
    {
        var snapshot = RealtimePerformanceTracker.CaptureSnapshot();

        Assert.True(snapshot.WorkingSetMb > 0, "WorkingSetMb should be greater than 0.");
        Assert.True(snapshot.PrivateWorkingSetMb > 0, "PrivateWorkingSetMb should be greater than 0.");
        Assert.True(snapshot.TotalWorkingSetMb >= snapshot.PrivateWorkingSetMb * 0.8, "TotalWorkingSetMb should be at least comparable to PrivateWorkingSetMb.");
        Assert.True(snapshot.GcHeapMb > 0, "GcHeapMb should be greater than 0.");
        Assert.True(snapshot.PeakWorkingSetMb >= snapshot.WorkingSetMb * 0.5, "PeakWorkingSetMb should be reasonable.");
        Assert.NotNull(snapshot.ActiveFunction);
    }

    [Fact]
    public void GetMemoryMetrics_ReturnsConsistentMetrics()
    {
        var (privWs, totalWs, peakWs) = RealtimePerformanceTracker.GetMemoryMetrics();

        Assert.True(privWs > 0, "privWs should be positive.");
        Assert.True(totalWs > 0, "totalWs should be positive.");
        Assert.True(totalWs >= privWs, $"totalWs ({totalWs}) should be >= privWs ({privWs}).");
        Assert.True(peakWs >= totalWs * 0.5, "peakWs should be >= totalWs * 0.5.");
    }

    [Fact]
    public void TrackScope_SetsActiveFunctionAndRestoresOnDispose()
    {
        var initialFunc = RealtimePerformanceTracker.LatestActiveFunction;

        using (RealtimePerformanceTracker.TrackScope("TestFunction.Execute", "test-context"))
        {
            Assert.Equal("TestFunction.Execute", RealtimePerformanceTracker.LatestActiveFunction);
        }

        Assert.Equal(initialFunc, RealtimePerformanceTracker.LatestActiveFunction);
    }

    [Fact]
    public void AuditLifecycle_CompletesWithoutException()
    {
        var exception = Record.Exception(() =>
        {
            RealtimePerformanceTracker.AuditLifecycle("TestPhase.Verification", "unit-test-run", triggerGcAudit: false);
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Preferences_RealtimePerformanceOverlayEnabled_DefaultAndRoundtrip()
    {
        var prefs = new MainProgramPreferences();
        Assert.False(prefs.RealtimePerformanceOverlayEnabled);

        prefs.RealtimePerformanceOverlayEnabled = true;
        Assert.True(prefs.RealtimePerformanceOverlayEnabled);
    }
}
