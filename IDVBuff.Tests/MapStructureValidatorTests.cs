using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

/// <summary>
/// 受限搜索(RestrictSearchToLockedTransform=true)独立 chamfer 门槛的回归测试。
/// 背景:2560×1600 分辨率档把 MaximumChamferPixels 放宽到 6.0,受限窗口内的
/// 部分重叠假候选(chamfer 4.787)得以通过 WeakAbsoluteScore,再被覆盖率主导的
/// 跟踪置信度抬过门槛。受限搜索窗口很小,真位置在窗口内时 chamfer 必然低,
/// 因此独立门槛(默认 3.0)不随分辨率档放宽。
/// </summary>
public sealed class MapStructureValidatorTests
{
    private static MapStructureCandidate BuildCandidate(double chamferPixels) => new()
    {
        ChamferPixels = chamferPixels,
        EdgeCoverage = 0.60d,
        OccupancyCoverage = 0.60d,
        ConsistentPartitions = 3,
        CompositeCost = chamferPixels + 1.0d
    };

    [Fact]
    public void RestrictedSearchRejectsChamferAboveStrictLimit()
    {
        // 模拟 2560×1600 分辨率档:全局 MaximumChamferPixels 放宽到 6.0。
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 6.0d,
            RestrictedSearchMaximumChamferPixels = 3.0d
        };
        var candidate = BuildCandidate(chamferPixels: 4.0d);

        // 受限搜索:4.0 > min(6.0, 3.0) = 3.0 → 拒绝。
        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate, margin: 0.5d, requiredMargin: 0.04d, tuning,
                restrictedSearch: true));

        // 全局搜索:4.0 < 6.0 → 通过(行为不变)。
        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                candidate, margin: 0.5d, requiredMargin: 0.04d, tuning,
                restrictedSearch: false));
    }

    [Fact]
    public void RestrictedSearchAcceptsLowChamfer()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 6.0d,
            RestrictedSearchMaximumChamferPixels = 3.0d
        };
        // 正确对齐量级 chamfer ≈ 2.4,受限搜索不应误杀。
        var candidate = BuildCandidate(chamferPixels: 2.4d);

        Assert.Equal(
            MapStructureRejectionReason.None,
            MapStructureValidator.Validate(
                candidate, margin: 0.5d, requiredMargin: 0.04d, tuning,
                restrictedSearch: true));
    }

    [Fact]
    public void RestrictedSearchMaximumChamferPixels_RoundTrips()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            RestrictedSearchMaximumChamferPixels = 2.4d
        };

        var cloned = tuning.Clone();
        Assert.Equal(2.4d, cloned.RestrictedSearchMaximumChamferPixels);

        var json = JsonSerializer.Serialize(tuning);
        var restored =
            JsonSerializer.Deserialize<MapStructureRegistrationTuning>(json)!;
        Assert.Equal(2.4d, restored.RestrictedSearchMaximumChamferPixels);

        // Normalize():0 被 clamp 到最小值 0.5;NaN 回退默认 3.0。
        var clamped = new MapStructureRegistrationTuning
        {
            RestrictedSearchMaximumChamferPixels = 0d
        };
        clamped.Normalize();
        Assert.Equal(0.5d, clamped.RestrictedSearchMaximumChamferPixels);

        var invalid = new MapStructureRegistrationTuning
        {
            RestrictedSearchMaximumChamferPixels = double.NaN
        };
        invalid.Normalize();
        Assert.Equal(3.0d, invalid.RestrictedSearchMaximumChamferPixels);
    }
}
