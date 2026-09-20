using IDVBuff.PluginContracts;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public sealed class PluginScreenshotContractTests
{
    [Fact]
    public void Defaults_UseThreeSecondSwitchDelay()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), PluginScreenshotDefaults.SwitchDelay);
    }

    [Fact]
    public void SuccessResult_ExposesPngImageAndMetadata()
    {
        var capturedAt = DateTimeOffset.UtcNow;
        var screenshot = new PluginScreenshot([1, 2, 3], 640, 480, capturedAt);
        var result = PluginScreenshotResult.Success(
            screenshot,
            TimeSpan.FromSeconds(3));

        Assert.True(result.Succeeded);
        Assert.Same(screenshot, result.Screenshot);
        Assert.Equal("image/png", result.Screenshot!.ContentType);
        Assert.Equal(640, result.Screenshot.Width);
        Assert.Equal(480, result.Screenshot.Height);
        Assert.Equal(capturedAt, result.Screenshot.CapturedAt);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Elapsed);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void FailureResult_ContainsReasonAndNoImage()
    {
        var result = PluginScreenshotResult.Failure(
            "请切换到被监测进程。",
            TimeSpan.FromSeconds(3));

        Assert.False(result.Succeeded);
        Assert.Null(result.Screenshot);
        Assert.Equal("请切换到被监测进程。", result.FailureReason);
    }
}
