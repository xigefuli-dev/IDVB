using IDVBuff.Survey.Contracts;

namespace IDVBuff.Tests;

public sealed class SurveyRegistrationTuningTests
{
    [Fact]
    public void DefaultScaleRangeCoversPointFourThroughPointOneSix()
    {
        var tuning = new SurveyRegistrationTuning();

        Assert.Equal(0.40d, tuning.MinimumScale);
        Assert.Equal(1.60d, tuning.MaximumScale);
        tuning.Validate();
    }
}
