using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class CaptureStreamDemandTests
{
    [Fact]
    public void ClosedMapExpiresButRapidReopenKeepsWarmStream()
    {
        var demand = new CaptureStreamDemand();
        demand.Begin(100);
        demand.End(200);
        Assert.False(demand.CanRelease(2199));
        demand.Request(2199);
        Assert.False(demand.CanRelease(2200));
        Assert.True(demand.CanRelease(4199));
    }

    [Fact]
    public void PendingReadbackCannotBeDisposedEvenWhenItRunsLong()
    {
        var demand = new CaptureStreamDemand();
        demand.Begin(100);
        demand.Begin(110);
        demand.End(120);
        Assert.False(demand.CanRelease(10000));
        demand.End(10001);
        Assert.False(demand.CanRelease(12000));
        Assert.True(demand.CanRelease(12001));
    }

    [Fact]
    public void ContinuousMiniMapRequestsPreventIdleShutdown()
    {
        var demand = new CaptureStreamDemand();
        for (var now = 0; now < 10000; now += 16)
        {
            demand.Begin(now);
            demand.End(now + 5);
            Assert.False(demand.CanRelease(now + 16));
        }
    }
}
