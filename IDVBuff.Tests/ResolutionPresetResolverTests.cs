using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;
using ResolutionTuningProfile = IDVBuff.Core.Models.ResolutionTuningProfile;

namespace IDVBuff.Tests;

public sealed class ResolutionPresetResolverTests
{
    private static List<ResolutionTuningProfile> SampleProfiles() =>
    [
        new() { Name = "1920x1080 @ 120 DPI", ClientWidth = 1920, ClientHeight = 1080, Dpi = 120 },
        new() { Name = "2560x1440 @ 120 DPI", ClientWidth = 2560, ClientHeight = 1440, Dpi = 120 },
        new() { Name = "3440x1440 @ 120 DPI", ClientWidth = 3440, ClientHeight = 1440, Dpi = 120 },
    ];

    [Fact]
    public void MatchPresetName_ExactMatch_ReturnsProfileName()
    {
        var result = ResolutionPresetResolver.MatchPresetName(
            SampleProfiles(), 2560, 1440, 120);

        Assert.Equal("2560x1440 @ 120 DPI", result);
    }

    [Fact]
    public void MatchPresetName_FuzzyMatch_WithinTolerance_ReturnsNearest()
    {
        // 尺寸差在 ±100px 内（窗口边框 / DPI 缩放偏移）→ 命中最接近的预设
        var result = ResolutionPresetResolver.MatchPresetName(
            SampleProfiles(), 1930, 1070, 120);

        Assert.Equal("1920x1080 @ 120 DPI", result);
    }

    [Fact]
    public void MatchPresetName_RatioFallback_MatchesAspectRatio()
    {
        // 宽高比回退：3440x1440 的 21:9 比例（尺寸差超出 ±100px）应命中 3440x1440
        var result = ResolutionPresetResolver.MatchPresetName(
            SampleProfiles(), 4000, 1674, 120);

        Assert.Equal("3440x1440 @ 120 DPI", result);
    }

    [Fact]
    public void MatchPresetName_EmptyProfiles_ReturnsNull()
    {
        var result = ResolutionPresetResolver.MatchPresetName(
            [], 1920, 1080, 120);

        Assert.Null(result);
    }

    [Fact]
    public void ResolveEffectivePreset_WindowGeometry_WinsOverExplicitSelection()
    {
        // 记住的选择不得覆盖当前窗口的实际分辨率。
        var result = ResolutionPresetResolver.ResolveEffectivePreset(
            "2560x1440 @ 120 DPI", SampleProfiles(), 1920, 1080, 120);

        Assert.Equal("1920x1080 @ 120 DPI", result);
    }

    [Fact]
    public void ResolveEffectivePreset_StaleSelection_FallsBackToAuto()
    {
        // 指定配置已从列表消失 → 回退自动匹配
        var result = ResolutionPresetResolver.ResolveEffectivePreset(
            "不存在的预设", SampleProfiles(), 1920, 1080, 120);

        Assert.Equal("1920x1080 @ 120 DPI", result);
    }

    [Fact]
    public void ResolveEffectivePreset_Auto_MatchesWindow()
    {
        var result = ResolutionPresetResolver.ResolveEffectivePreset(
            null, SampleProfiles(), 2560, 1440, 120);

        Assert.Equal("2560x1440 @ 120 DPI", result);
    }
}
