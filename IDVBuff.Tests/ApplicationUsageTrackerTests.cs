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
    [InlineData(0, "0 分 0 秒")]
    [InlineData(59, "0 分 59 秒")]
    [InlineData(60, "1 分 0 秒")]
    [InlineData(3599, "59 分 59 秒")]
    [InlineData(3600, "1 小时 0 分")]
    [InlineData(90061, "1 天 1 小时")]
    public void DisplayHandlesTimeUnitBoundaries(int seconds, string expected) =>
        Assert.Equal(expected, ApplicationUsageTracker.FormatDuration(TimeSpan.FromSeconds(seconds)));

    public void Dispose()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root));
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
