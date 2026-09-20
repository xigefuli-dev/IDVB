using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit;

namespace IDVBuff.Tests;
public sealed partial class MapStructureRegistrarTests
{

    [Fact]
    public void LegacySearch_ReportsSubstageTimings()
    {
        using var reference = BuildReference();
        var crop = new Rect(82, 58, 300, 236);
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(0d, 0d, live.Width, live.Height);
        var tuning = TestTuning();
        tuning.EnableFastAlignment = false;
        tuning.EnableVisibleAwareInjection = false;
        tuning.EnableVisibleAwareShadow = false;

        var registrar = new MapStructureRegistrar(new MapStructurePreprocessor());
        var result = registrar.Register(new MapStructureRegistrationRequest
        {
            ReferenceImage = reference,
            LiveRoi = live,
            ViewportBounds = viewport,
            LockedTransform = Locked(reference),
            Tuning = tuning,
            AllowScaleSearch = false
        });

        Assert.True(result.Accepted, result.FailureReason);
        Assert.False(result.UsedFastStrategy);
        Assert.True(result.SearchMilliseconds > 0d);
        Assert.True(result.DistanceMapMilliseconds >= 0d);
        Assert.True(result.QueryConstructionMilliseconds >= 0d);
        Assert.True(result.HistoryCandidateMilliseconds >= 0d);
        Assert.True(result.FeatureVotingMilliseconds >= 0d);
        Assert.True(result.PyramidSearchMilliseconds >= 0d);
        Assert.True(result.LocalTemplateSearchMilliseconds >= 0d);
        Assert.True(result.GlobalTemplateSearchMilliseconds >= 0d);
        Assert.True(result.CandidateRankingMilliseconds >= 0d);
    }

}
