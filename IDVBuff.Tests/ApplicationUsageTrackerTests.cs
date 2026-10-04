using IDVBuff.Lifecycle;

namespace IDVBuff.Tests;

public sealed class ApplicationUsageTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"idvb-application-usage-{Guid.NewGuid():N}");
    private string StatisticsPath => Path.Combine(_root, "application-usage.json");

    [Fact]
    public void CheckpointsAndRestartDoNotCountTheSameSessionTwice()
    {
        var elapsed = TimeSpan.Zero;
        using (var first = new ApplicationUsageTracker(StatisticsPath, () => elapsed, false))
        {
            first.Start();
            elapsed = TimeSpan.FromSeconds(60);
            first.SaveCheckpoint();
            elapsed = TimeSpan.FromSeconds(90);
            first.SaveCheckpoint();
        }
        elapsed = TimeSpan.FromSeconds(20);
        using var restarted = new ApplicationUsageTracker(StatisticsPath, () => elapsed, false);
        restarted.Start();
        Assert.Equal(TimeSpan.FromSeconds(110), restarted.GetSnapshot().Total);
        Assert.Equal(TimeSpan.FromSeconds(20), restarted.GetSnapshot().Session);
    }

    [Fact]
    public void StartIsIdempotentAndSnapshotsContinueWithoutNavigation()
    {
        var elapsed = TimeSpan.Zero;
        using var tracker = new ApplicationUsageTracker(StatisticsPath, () => elapsed, false);
        Assert.Equal(TimeSpan.Zero, tracker.GetSnapshot().Session);
        tracker.Start();
        elapsed = TimeSpan.FromSeconds(15);
        tracker.Start();
        Assert.Equal(TimeSpan.FromSeconds(15), tracker.GetSnapshot().Total);
        elapsed = TimeSpan.FromSeconds(90);
        Assert.Equal(TimeSpan.FromSeconds(90), tracker.GetSnapshot().Total);
    }

    [Fact]
    public void ExitCommitsFinalElapsedAndLateCallbacksCannotOverwriteIt()
    {
        var elapsed = TimeSpan.FromSeconds(12);
        var tracker = new ApplicationUsageTracker(StatisticsPath, () => elapsed, false);
        tracker.Start();
        tracker.Dispose();
        var stored = File.ReadAllText(StatisticsPath);
        elapsed = TimeSpan.FromHours(5);
        tracker.SaveCheckpoint();
        tracker.Dispose();
        Assert.Equal(stored, File.ReadAllText(StatisticsPath));
        Assert.Equal(TimeSpan.FromSeconds(12), tracker.GetSnapshot().Session);
        using var reloaded = new ApplicationUsageTracker(StatisticsPath, periodicSave: false);
        Assert.Equal(TimeSpan.FromSeconds(12), reloaded.GetSnapshot().Total);
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("{\"SchemaVersion\":2,\"TotalTicks\":20}")]
    [InlineData("{\"SchemaVersion\":1,\"TotalTicks\":-20}")]
    public void UnreadableHistoryIsPreservedAndSessionRemainsVisible(string original)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatisticsPath, original);
        using (var tracker = new ApplicationUsageTracker(StatisticsPath,
            () => TimeSpan.FromSeconds(9), false))
        {
            tracker.Start();
            tracker.SaveCheckpoint();
            Assert.Null(tracker.GetSnapshot().Total);
            Assert.Equal(TimeSpan.FromSeconds(9), tracker.GetSnapshot().Session);
        }
        Assert.Equal(original, File.ReadAllText(StatisticsPath));
    }

    [Fact]
    public void UnstartedHelperLifetimeDoesNotCreateUsageData()
    {
        using (var tracker = new ApplicationUsageTracker(StatisticsPath,
            () => TimeSpan.FromMinutes(10), false))
        {
            tracker.SaveCheckpoint();
            Assert.Equal(TimeSpan.Zero, tracker.GetSnapshot().Total);
        }
        Assert.False(File.Exists(StatisticsPath));
    }

    [Theory]
    [InlineData(0, "0.0 h")]
    [InlineData(245, "0.1 h")]
    [InlineData(3600, "1.0 h")]
    [InlineData(90061, "25.0 h")]
    [InlineData(1082880, "300.8 h")]
    public void DisplayHandlesTimeUnitBoundaries(int seconds, string expected) =>
        Assert.Equal(expected, ApplicationUsageTracker.FormatDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ExistingFourMinuteHistoryKeepsGrowingAcrossLongSessionsAndRestart()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(StatisticsPath, "{\"SchemaVersion\":1,\"TotalTicks\":2454236846}");
        var elapsed = TimeSpan.Zero;
        using (var tracker = new ApplicationUsageTracker(StatisticsPath, () => elapsed, false))
        {
            tracker.Start();
            elapsed = TimeSpan.FromSeconds(246);
            tracker.SaveCheckpoint();
            Assert.True(tracker.GetSnapshot().Total > TimeSpan.FromSeconds(491));
            elapsed = TimeSpan.FromHours(300.8);
            tracker.SaveCheckpoint();
            tracker.SaveCheckpoint();
            Assert.Equal("300.9 h", ApplicationUsageTracker.FormatDuration(tracker.GetSnapshot().Total!.Value));
        }
        using var restarted = new ApplicationUsageTracker(StatisticsPath, periodicSave: false);
        Assert.Equal(TimeSpan.FromTicks(2454236846) + elapsed, restarted.GetSnapshot().Total);
    }

    [Fact]
    public void CoexistingGuiTrackersMergeIntervalsWithoutOverwritingOrDoubleCounting()
    {
        var firstElapsed = TimeSpan.Zero;
        var secondElapsed = TimeSpan.Zero;
        using (var first = new ApplicationUsageTracker(StatisticsPath, () => firstElapsed, false))
        using (var second = new ApplicationUsageTracker(StatisticsPath, () => secondElapsed, false))
        {
            first.Start();
            second.Start();
            firstElapsed = TimeSpan.FromSeconds(300);
            secondElapsed = TimeSpan.FromSeconds(400);
            Parallel.Invoke(first.SaveCheckpoint, second.SaveCheckpoint);
            firstElapsed = TimeSpan.FromSeconds(500);
            secondElapsed = TimeSpan.FromSeconds(600);
            Parallel.Invoke(second.SaveCheckpoint, first.SaveCheckpoint);
            first.SaveCheckpoint();
            Assert.Equal(TimeSpan.FromSeconds(1100), first.GetSnapshot().Total);
        }
        using var reloaded = new ApplicationUsageTracker(StatisticsPath, periodicSave: false);
        Assert.Equal(TimeSpan.FromSeconds(1100), reloaded.GetSnapshot().Total);
    }

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
