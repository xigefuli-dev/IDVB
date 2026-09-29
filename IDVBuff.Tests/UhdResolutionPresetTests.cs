using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using IDVBuff.Infrastructure.Configuration;

namespace IDVBuff.Tests;

public sealed class UhdResolutionPresetTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public async Task PhysicalUhdSelectsPackagedPresetAndRestoresQhdOnSwitch(int dpi)
    {
        // A nonexistent user directory prevents machine-local overrides from
        // hiding missing/mispackaged built-in configuration.
        using var provider = new TomlConfigProvider(
            Path.Combine(Path.GetTempPath(), "IDVB-UHD-" + Guid.NewGuid().ToString("N")));
        using var profiles = new ResolutionProfileManager(provider);
        const string name = "3840x2160 @ 120 DPI";
        Assert.Equal(name, profiles.MatchProfile(3840, 2160, dpi));
        Assert.Equal(name, ResolutionPresetResolver.ResolveEffectivePreset(
            "1920x1080 @ 120 DPI", profiles.GetAvailableProfiles(), 3840, 2160, dpi));
        Assert.True(await profiles.TryAutoMatchAsync(3840, 2160, dpi));
        Assert.Equal(name, provider.ActiveResolutionPreset);

        var side = provider.Get<SideEntranceScanConfig>("side_entrance");
        Assert.Equal(0.675, side.MinimumScale, 8);
        Assert.Equal(5.0, side.MaximumScale);
        var low = provider.Get<LowStructureConfig>("low_structure");
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure(low);
        Assert.Equal(0.525, tuning.LowStructureMinimumScale, 8);
        Assert.Equal(2.55, tuning.LowStructureMaximumScale, 8);
        Assert.Equal(15, tuning.LowStructureScaleHypothesisCount);
        Assert.Equal(0.005, tuning.ScaleSearchStep, 8);
        Assert.Equal(3.0, tuning.MaximumChamferPixels);
        Assert.True(tuning.MinimumEdgeCoverage >= 0.75);
        Assert.Equal(0.005, provider.Get<ScaleConfig>("scale").SearchStep, 8);
        Assert.Equal(0.72, provider.Get<IDVBuff.Features.Maps.GateConfig>("gate").MatchThreshold, 8);

        var qhdScales = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(.35, 1.70, 15);
        var uhdScales = MapStructureScaleSearch.BuildLowStructureScaleHypotheses(
            tuning.LowStructureMinimumScale, tuning.LowStructureMaximumScale,
            tuning.LowStructureScaleHypothesisCount);
        Assert.Equal(qhdScales.Count, uhdScales.Count);
        for (var i = 0; i < qhdScales.Count; i++)
            Assert.Equal(qhdScales[i] * 1.5, uhdScales[i], 8);

        // This exercises reload rather than reusing the UHD objects. Neither
        // resolution may leave overrides in the next run's merged table.
        Assert.True(await profiles.TryAutoMatchAsync(2560, 1440, dpi));
        Assert.Equal(0.45, provider.Get<SideEntranceScanConfig>("side_entrance").MinimumScale, 8);
        Assert.Equal(1.70, provider.Get<LowStructureConfig>("low_structure").MaximumScale, 8);
        Assert.Equal(0.01, provider.Get<ScaleConfig>("scale").SearchStep, 8);
        Assert.True(await profiles.TryAutoMatchAsync(3840, 2160, dpi));
        Assert.Equal(2.55, provider.Get<LowStructureConfig>("low_structure").MaximumScale, 8);
    }

    [Fact]
    public void UhdSidePolicyConsumesBoundsWithoutExpandingDeadline()
    {
        using var provider = new TomlConfigProvider(
            Path.Combine(Path.GetTempPath(), "IDVB-UHD-" + Guid.NewGuid().ToString("N")));
        provider.SetActivePreset("3840x2160");
        try
        {
            SideEntranceScanRules.ApplyConfig(provider);
            foreach (var mode in Enum.GetValues<ScanPerformanceMode>())
            {
                var policy = ScanExecutionPolicy.For(mode);
                Assert.Equal(0.675, policy.MinimumScale, 8);
                Assert.Equal(5.0, policy.MaximumScale);
                Assert.InRange(policy.BudgetMilliseconds, 1, 1000);
            }
        }
        finally
        {
            SideEntranceScanRules.ApplyConfig(new SideEntranceScanConfig());
        }
    }

    [Fact]
    public void UhdCacheStillRequiresExactPhysicalResolutionAndViewport()
    {
        var uhd = new MapCacheResolutionSignature(3840, 2160, 2400, 1800);
        Assert.True(uhd.IsSupported);
        Assert.False((uhd with { ClientWidth = 3839 }).IsSupported);
        Assert.False((uhd with { ViewportHeight = 0 }).IsSupported);
        Assert.NotEqual(uhd, new MapCacheResolutionSignature(2560, 1440, 1600, 1200));
        Assert.NotEqual(uhd, uhd with { ViewportWidth = 2399 });
    }
}
