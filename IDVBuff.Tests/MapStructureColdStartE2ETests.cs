using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

/// <summary>
/// 契约回归的端到端部分：在真实生产入口 <c>AlignWithCachedScale</c> →
/// <c>AlignStructureOnly</c> 上验证「中性种子不是尺度证据」。
/// <para>
/// 实测日志里 <c>scaleSearchPolicy=Fixed | scaleSeed=1 | hypotheses=1</c> 正是从
/// 这条路径出来的（registrar 的 <c>baselineScale</c> 只是 seed 除以
/// physical/computation 比例，所以 seed=1 对应 baselineScale≈0.7564）。这里断言
/// 真实入口的日志与搜索假设数量，而不只是纯规则函数。
/// </para>
/// </summary>
public sealed class MapStructureColdStartE2ETests
{
    private const string FloorKey = CompleteAlignmentTestScenario.UpperFloor;

    private static MapOverlayTransform NeutralSeed(
        CompleteAlignmentTestScenario scenario) =>
        MapFloorScaleSeedRules.CreateIndependentFloorSeed(
            scenario.Map,
            FloorKey);

    /// <summary>一次真实测量得到的非中性变换（scale ≠ 1 且带平移偏移）。</summary>
    private static MapOverlayTransform MeasuredSeed() => new()
    {
        ScaleX = 0.736d,
        ScaleY = 0.736d,
        OffsetX = -128d,
        OffsetY = 96d,
        ReferenceCenterX = 360d,
        ReferenceCenterY = 270d,
        ScreenCenterX = 240d,
        ScreenCenterY = 390d,
        ReferenceWidth = 720,
        ReferenceHeight = 540,
        AlignmentMode = MapOverlayAlignmentMode.Uniform
    };

    private static CapturedGameFrame CreateFrame(
        CompleteAlignmentTestScenario scenario)
    {
        var crop = new Rect(60, 40, 420, 320);
        return scenario.FloorFrame(
            FloorKey,
            crop,
            new MapScreenRect(600d, 320d, crop.Width, crop.Height));
    }

    private static MapLogEntry RequireEntry(
        MapLogCollector collector,
        string message)
    {
        var entry = collector.GetEntries()
            .LastOrDefault(item => item.Message == message);
        Assert.NotNull(entry);
        return entry;
    }

    [Fact]
    public async Task NeutralSeedOnTheCachedScaleRouteBecomesAGlobalScaleSearch()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var neutralSeed = NeutralSeed(scenario);
        Assert.True(
            MapFloorScaleSeedRules.IsNeutralIndependentSeed(neutralSeed),
            "前置条件：本楼层的独立种子必须是中性占位符。");

        using var frame = CreateFrame(scenario);
        using var collector = new MapLogCollector { IsEnabled = true };
        var previous = MapLogCollector.Instance;
        MapLogCollector.Instance = collector;
        try
        {
            // 调用方写死 Fixed —— 这正是缓存/VPSG 路线上的写法。
            _ = scenario.Service.AlignWithCachedScale(
                frame,
                scenario.Map.Id,
                FloorKey,
                neutralSeed,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning,
                CompleteAlignmentTestScenario.StructureTuning,
                identityPriorConfidence: 0d,
                restrictTranslationToSeed: true);
        }
        finally
        {
            MapLogCollector.Instance = previous;
        }

        var seal = RequireEntry(
            collector,
            $"中性结构种子冷启动 · floor={FloorKey} · 已封锁 Fixed 占位尺度");
        Assert.Equal("neutral", seal.Details!["seedKind"]);
        Assert.Equal(true, seal.Details["unknownTransformColdStart"]);
        Assert.Equal("Search", seal.Details["scaleSearchPolicy"]);
        Assert.Equal(1d, seal.Details["scaleSeed"]);
        Assert.Equal(false, seal.Details["restrictedSearch"]);
        Assert.Equal(true, seal.Details["disableScaleEarlyTermination"]);
        Assert.Equal(false, seal.Details["enableFastAlignment"]);
        var sealedRadius = Assert.IsType<double>(seal.Details["scaleSearchRadius"]);
        Assert.True(
            sealedRadius >= MapFloorScaleSearchPolicy.UncalibratedMaximumScale - 1d - 1e-9,
            $"封锁后的半径 {sealedRadius:F4} 必须覆盖未标定全局区间。");

        // 真正到达 registrar 的请求：必须是宽半径、非受限、且不再只有一个假设。
        var request = RequireEntry(collector, "开始结构配准");
        Assert.Equal(
            MapScaleSearchPolicy.Search.ToString(),
            request.Details!["scaleSearchPolicy"]?.ToString());
        Assert.Equal(false, request.Details["trackingMode"]);

        var search = collector.GetEntries()
            .Last(item => item.Message.StartsWith(
                "结构搜索完成",
                StringComparison.Ordinal));
        Assert.Equal(false, search.Details!["usedRestrictedSearch"]);
        var radius = Assert.IsType<double>(search.Details["scaleSearchRadius"]);
        Assert.True(
            radius > 0.5d,
            $"进入 registrar 的半径只有 {radius:F4}，说明仍停留在默认局部搜索。");
        var hypotheses = Assert.IsType<int>(search.Details["hypotheses"]);
        Assert.True(
            hypotheses > 1,
            $"封锁后仍然只造出 {hypotheses} 个尺度假设，等于把尺度锁在错误值上。");
    }

    [Fact]
    public async Task MeasuredSeedOnTheCachedScaleRouteKeepsItsFixedValidation()
    {
        await using var scenario = await CompleteAlignmentTestScenario.CreateAsync();
        var measuredSeed = MeasuredSeed();
        Assert.False(
            MapFloorScaleSeedRules.IsNeutralIndependentSeed(measuredSeed));

        using var frame = CreateFrame(scenario);
        using var collector = new MapLogCollector { IsEnabled = true };
        var previous = MapLogCollector.Instance;
        MapLogCollector.Instance = collector;
        try
        {
            _ = scenario.Service.AlignWithCachedScale(
                frame,
                scenario.Map.Id,
                FloorKey,
                measuredSeed,
                MapOverlayAlignmentMode.Uniform,
                CompleteAlignmentTestScenario.RecognitionTuning,
                CompleteAlignmentTestScenario.StructureTuning);
        }
        finally
        {
            MapLogCollector.Instance = previous;
        }

        // 正向对照：真实缓存/VPSG 候选的固定尺度校验必须原样保留，
        // 冷启动防线不得把它升级成昂贵的全局搜索。
        Assert.DoesNotContain(
            collector.GetEntries(),
            item => item.Message.StartsWith(
                "中性结构种子冷启动",
                StringComparison.Ordinal));

        var request = RequireEntry(collector, "开始结构配准");
        Assert.Equal(
            MapScaleSearchPolicy.Fixed.ToString(),
            request.Details!["scaleSearchPolicy"]?.ToString());

        var search = collector.GetEntries()
            .Last(item => item.Message.StartsWith(
                "结构搜索完成",
                StringComparison.Ordinal));
        Assert.Equal(1, Assert.IsType<int>(search.Details!["hypotheses"]));
        Assert.Equal(true, search.Details["usedRestrictedSearch"]);
    }
}
