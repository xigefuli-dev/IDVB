using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

/// <summary>
/// Chamfer 绝对门槛的回归测试。全局搜索与受限搜索都必须能拦截
/// 仅靠局部重叠取得高分、但结构距离已经超过安全范围的假候选。
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
    public void ChamferComparisonUsesScreenPixelsAcrossScaleHypotheses()
    {
        var runaway = MapStructureEvaluator.NormalizeChamferToScreenPixels(
            referencePixels: 4.12d,
            hypothesisScale: 1.6d);
        var expected = MapStructureEvaluator.NormalizeChamferToScreenPixels(
            referencePixels: 4.68d,
            hypothesisScale: 0.504d);

        Assert.Equal(6.592d, runaway, 3);
        Assert.Equal(2.35872d, expected, 5);
        Assert.True(expected < runaway);
        Assert.True(expected < 3d);
        Assert.True(runaway > 3d);
    }

    [Fact]
    public void EdgeToleranceRemainsConstantInScreenSpace()
    {
        const double screenTolerance = 2.25d;
        foreach (var scale in new[] { 0.504d, 1d, 1.6d })
        {
            var referenceTolerance =
                MapStructureEvaluator.ConvertScreenToleranceToReferencePixels(
                    screenTolerance,
                    scale);
            Assert.Equal(screenTolerance, referenceTolerance * scale, 12);
        }
    }

    [Fact]
    public void ScreenSpaceNormalizationIsIsolatedToLowStructureChannel()
    {
        const double referenceChamfer = 4.68d;
        const double configuredTolerance = 2.25d;
        const double scale = 0.504d;

        Assert.Equal(
            referenceChamfer,
            MapStructureEvaluator.ResolveChamferPixels(
                referenceChamfer,
                scale,
                MapAlignmentChannel.Standard),
            12);
        Assert.Equal(
            configuredTolerance,
            MapStructureEvaluator.ResolveEdgeTolerancePixels(
                configuredTolerance,
                scale,
                MapAlignmentChannel.Standard),
            12);

        Assert.Equal(
            2.35872d,
            MapStructureEvaluator.ResolveChamferPixels(
                referenceChamfer,
                scale,
                MapAlignmentChannel.LowStructure),
            5);
        Assert.Equal(
            configuredTolerance,
            MapStructureEvaluator.ResolveEdgeTolerancePixels(
                configuredTolerance,
                scale,
                MapAlignmentChannel.LowStructure) * scale,
            12);
    }

    [Fact]
    public void EverySearchModeRejectsChamferAboveLockedLimit()
    {
        // 旧配置即使尝试放宽，全局和受限搜索也必须保持 3.0。
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 6.0d,
            RestrictedSearchMaximumChamferPixels = 3.0d
        };
        var candidate = BuildCandidate(chamferPixels: 4.0d);

        Assert.Equal(3.0d, tuning.MaximumChamferPixels);
        Assert.Equal(3.0d, tuning.RestrictedSearchMaximumChamferPixels);

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate, margin: 0.5d, requiredMargin: 0.04d, tuning,
                restrictedSearch: true));

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate, margin: 0.5d, requiredMargin: 0.04d, tuning,
                restrictedSearch: false));
    }

    [Fact]
    public void ThreePixelGlobalLimitRejectsKnownLargeScaleFalseFit()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 3.0d,
            RestrictedSearchMaximumChamferPixels = 3.0d
        };
        // 取自 B1F 错误 scale 锁定后仍被接受的一次全局候选量级。
        var candidate = BuildCandidate(chamferPixels: 4.1846d);

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate, margin: 0.148d, requiredMargin: 0.0375d, tuning,
                restrictedSearch: false));
    }

    [Fact]
    public void LowStructureThresholdsRejectWeakEdgeFitDespiteOtherEvidence()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var candidate = new MapStructureCandidate
        {
            ChamferPixels = 2.36d,
            ReverseChamferPixels = 2.36d,
            EdgeCoverage = 0.534d,
            OccupancyCoverage = 0.863d,
            ReferenceCoverage = 0.60d,
            ProjectionCorrelation = 0.80d,
            ConsistentPartitions = 1,
            CompositeCost = 12.194d
        };

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate,
                margin: 0.156d,
                requiredMargin: tuning.MinimumCandidateMargin,
                tuning,
                restrictedSearch: false));
    }

    [Fact]
    public void SparseAppearanceCandidateStillRequiresStructureQuality()
    {
        var tuning = MapAlignmentChannelRegistry.CreateLowStructure();
        var candidate = new MapStructureCandidate
        {
            ChamferPixels = 2.10d,
            EdgeCoverage = 0.32d,
            OccupancyCoverage = 0.15d,
            ReferenceCoverage = 0.62d,
            ProjectionCorrelation = 0.50d,
            ConsistentPartitions = 1
        };
        var request = new MapStructureRegistrationRequest
        {
            Channel = MapAlignmentChannel.LowStructure,
            ScaleSearchPolicy = MapScaleSearchPolicy.Fixed,
            RestrictSearchToLockedTransform = true
        };

        Assert.Equal(
            MapStructureRejectionReason.WeakAbsoluteScore,
            MapStructureValidator.Validate(
                candidate,
                margin: 1d,
                requiredMargin: tuning.MinimumCandidateMargin,
                tuning,
                restrictedSearch: true,
                request));
    }


    [Fact]
    public void Preset2560x1600UsesThreePixelGlobalChamferLimit()
    {
        var presetPath = Path.Combine(
            FindRepositoryRoot(),
            "Infrastructure",
            "Configuration",
            "Presets",
            "2560x1600",
            "alignment.toml");
        var maximumChamferLine = File.ReadLines(presetPath)
            .Single(line => line.TrimStart().StartsWith(
                "maximum_chamfer_pixels =", StringComparison.Ordinal));

        Assert.Equal("maximum_chamfer_pixels = 3.0", maximumChamferLine.Trim());
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
    public void ChamferHardLimitSurvivesCloneSerializationAndInvalidAssignments()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            RestrictedSearchMaximumChamferPixels = 2.4d
        };

        var cloned = tuning.Clone();
        Assert.Equal(3.0d, cloned.RestrictedSearchMaximumChamferPixels);

        var json = JsonSerializer.Serialize(tuning);
        var restored =
            JsonSerializer.Deserialize<MapStructureRegistrationTuning>(json)!;
        Assert.Equal(3.0d, restored.RestrictedSearchMaximumChamferPixels);

        // 任何数值（包括 0 和 NaN）都不能改变硬锁。
        var clamped = new MapStructureRegistrationTuning
        {
            RestrictedSearchMaximumChamferPixels = 0d
        };
        clamped.Normalize();
        Assert.Equal(3.0d, clamped.RestrictedSearchMaximumChamferPixels);

        var invalid = new MapStructureRegistrationTuning
        {
            RestrictedSearchMaximumChamferPixels = double.NaN
        };
        invalid.Normalize();
        Assert.Equal(3.0d, invalid.RestrictedSearchMaximumChamferPixels);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
