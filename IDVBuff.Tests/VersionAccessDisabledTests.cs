using System.Reflection;
using IDVBuff.Features.Accounts;
using Xunit;

namespace IDVBuff.Tests;

public sealed class VersionAccessDisabledTests
{
    [Fact]
    public async Task DisabledCheckSkipsInputsAndNeverCreatesNetworkClient()
    {
        Assert.False(VersionAccessClient.Enabled);
        // Neither invalid input nor cancellation may enter the validation pipeline.
        var result = await VersionAccessClient.CheckAsync(null!, new CancellationToken(true));
        Assert.True(result.Allowed);
        Assert.False(result.LoginRequired);
        Assert.False(result.UpgradeRequired);
        Assert.Equal("", result.Message);
        AssertNetworkClientNotCreated();
    }

    [Fact]
    public async Task DisabledHeadlessMonitorCompletesWithoutStartingTimer()
    {
        var monitor = VersionAccessClient.MonitorHeadlessAsync(null!);
        Assert.True(monitor.IsCompletedSuccessfully);
        await monitor;
        AssertNetworkClientNotCreated();
    }

    private static void AssertNetworkClientNotCreated()
    {
        var field = typeof(VersionAccessClient).GetField("Http", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.False(Assert.IsType<Lazy<System.Net.Http.HttpClient>>(field.GetValue(null)).IsValueCreated);
    }
}
