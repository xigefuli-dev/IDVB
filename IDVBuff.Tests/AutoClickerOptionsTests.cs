using IDVBuff.Plugins.AutoClicker;
using Xunit;

namespace IDVBuff.Tests;

public class AutoClickerOptionsTests
{
    [Fact]
    public void Defaults_SumToTheHistorical15msPeriod()
    {
        var options = new AutoClickerOptions();

        Assert.Equal(5, options.KeyDownDelayMilliseconds);
        Assert.Equal(10, options.UpToNextDownDelayMilliseconds);
        Assert.Equal(15, options.TotalPeriodMilliseconds);
    }

    [Fact]
    public void Setters_ClampToBoundedRanges()
    {
        var options = new AutoClickerOptions();

        options.KeyDownDelayMilliseconds = 0;
        Assert.Equal(1, options.KeyDownDelayMilliseconds);

        options.KeyDownDelayMilliseconds = 999;
        Assert.Equal(AutoClickerOptions.MaxKeyDownDelayMilliseconds, options.KeyDownDelayMilliseconds);

        options.UpToNextDownDelayMilliseconds = -5;
        Assert.Equal(1, options.UpToNextDownDelayMilliseconds);

        options.UpToNextDownDelayMilliseconds = 9999;
        Assert.Equal(AutoClickerOptions.MaxUpToNextDownDelayMilliseconds, options.UpToNextDownDelayMilliseconds);
    }

    [Theory]
    [InlineData(5, 50)]
    [InlineData(15, 150)]
    public void TickHelpers_ConvertMillisecondsAtGivenRate(int milliseconds, long expected)
    {
        var options = new AutoClickerOptions { KeyDownDelayMilliseconds = milliseconds };
        Assert.Equal(expected, options.KeyDownTicks(tickRate: 10_000));
    }

    [Fact]
    public void PeriodTicks_IsDownPlusUp()
    {
        var options = new AutoClickerOptions
        {
            KeyDownDelayMilliseconds = 7,
            UpToNextDownDelayMilliseconds = 23
        };

        Assert.Equal(7 + 23, options.TotalPeriodMilliseconds);
        Assert.Equal((7 + 23) * 10L, options.PeriodTicks(tickRate: 10_000));
    }
}
