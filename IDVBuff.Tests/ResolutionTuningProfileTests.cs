using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;

public sealed partial class ResolutionTuningProfileTests
{
    // ═══════════════════════════════════════════════════════════════
    // Match() — 精确匹配
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_ExactMatch_ReturnsMatchingProfile()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "A", ClientWidth = 1920, ClientHeight = 1080, Dpi = 120 },
            new() { Name = "B", ClientWidth = 2560, ClientHeight = 1440, Dpi = 120 },
        };

        var result = ResolutionTuningProfile.Match(profiles, 2560, 1440, 120);

        Assert.NotNull(result);
        Assert.Equal("B", result.Name);
    }

    [Fact]
    public void Match_ExactMatch_PrefersFirstWhenDuplicate()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "First", ClientWidth = 1920, ClientHeight = 1080, Dpi = 120 },
            new() { Name = "Second", ClientWidth = 1920, ClientHeight = 1080, Dpi = 120 },
        };

        var result = ResolutionTuningProfile.Match(profiles, 1920, 1080, 120);

        Assert.NotNull(result);
        Assert.Equal("First", result.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    // Match() — 模糊匹配
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_FuzzyMatch_WithinDefaultTolerance()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "Target", ClientWidth = 1920, ClientHeight = 1080, Dpi = 120 },
        };

        // 偏差在默认 tolerance 100px 内
        var result = ResolutionTuningProfile.Match(profiles, 1960, 1050, 120);

        Assert.NotNull(result);
        Assert.Equal("Target", result.Name);
    }

    [Fact]
    public void Match_FuzzyMatch_OutsideTolerance_NotMatched()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new()
            {
                Name = "Target",
                ClientWidth = 1920,
                ClientHeight = 1080,
                Dpi = 120,
                MatchTolerancePixels = 50
            },
        };

        // 偏差 80px 超出 tolerance 50px
        var result = ResolutionTuningProfile.Match(profiles, 2000, 1080, 120);

        // 精确匹配失败 + 模糊匹配失败 → 应走到宽高比匹配
        // 2000/1080 ≈ 1.852, 1920/1080 ≈ 1.778, ratio diff ≈ 0.074 > 0.05
        Assert.Null(result);
    }

    [Fact]
    public void Match_FuzzyMatch_UsesPerProfileTolerance()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new()
            {
                Name = "Narrow",
                ClientWidth = 1920,
                ClientHeight = 1080,
                Dpi = 120,
                MatchTolerancePixels = 10
            },
            new()
            {
                Name = "Wide",
                ClientWidth = 2560,
                ClientHeight = 1440,
                Dpi = 120,
                MatchTolerancePixels = 200
            },
        };

        // 偏差 150px — Narrow 的 tolerance 10 不够，Wide 的 tolerance 200 够
        var result = ResolutionTuningProfile.Match(profiles, 2520, 1350, 120);

        Assert.NotNull(result);
        // Wide 应在精确匹配（2520≠2560）失败后通过模糊匹配命中
        Assert.Equal("Wide", result.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    // Match() — DPI 仅作为同尺寸重复档案的次级偏好
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_DpiMismatch_ExactPhysicalSizeStillMatches()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "WrongDpi", ClientWidth = 1920, ClientHeight = 1080, Dpi = 96 },
        };

        var result = ResolutionTuningProfile.Match(profiles, 1920, 1080, 120);

        Assert.NotNull(result);
        Assert.Equal("WrongDpi", result.Name);
    }

    [Fact]
    public void Match_DpiMismatch_FuzzyPhysicalSizeStillMatches()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "WrongDpi", ClientWidth = 1920, ClientHeight = 1080, Dpi = 150 },
        };

        var result = ResolutionTuningProfile.Match(profiles, 1930, 1070, 120);

        Assert.NotNull(result);
        Assert.Equal("WrongDpi", result.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    // Match() — 宽高比降级匹配
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_AspectRatioFallback_WithinTolerance()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            // DPI 相同、宽高比相近（16:9 ≈ 1.778）
            new() { Name = "QHD", ClientWidth = 2560, ClientHeight = 1440, Dpi = 120 },
        };

        // 1920×1080 也是 16:9，宽高比 ≈ 1.778
        // 差值为 0，远小于 0.05
        var result = ResolutionTuningProfile.Match(profiles, 1920, 1080, 120);

        Assert.NotNull(result);
        Assert.Equal("QHD", result.Name);
    }

    [Fact]
    public void Match_AspectRatioFallback_OutsideTolerance()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "Wide16x9", ClientWidth = 2560, ClientHeight = 1440, Dpi = 120 },
        };

        // 2560×1600 = 16:10 = 1.6, vs 2560÷1440 ≈ 1.778
        // 差值 ≈ 0.178 > 0.05
        // 但精确匹配失败（1600≠1440），模糊匹配失败（差值 160 > 100），
        // 宽高比匹配也失败 → null
        var result = ResolutionTuningProfile.Match(profiles, 2560, 1600, 120);

        Assert.Null(result);
    }

    // ═══════════════════════════════════════════════════════════════
    // Match() — 边界情况
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Match_EmptyList_ReturnsNull()
    {
        var result = ResolutionTuningProfile.Match(
            Array.Empty<ResolutionTuningProfile>(), 1920, 1080, 120);

        Assert.Null(result);
    }

    [Fact]
    public void Match_MultipleCandidates_ExactPreferredOverFuzzy()
    {
        var profiles = new List<ResolutionTuningProfile>
        {
            new() { Name = "Fuzzy", ClientWidth = 1920, ClientHeight = 1080, Dpi = 120 },
            new() { Name = "Exact", ClientWidth = 2560, ClientHeight = 1440, Dpi = 120 },
        };

        var result = ResolutionTuningProfile.Match(profiles, 2560, 1440, 120);

        Assert.NotNull(result);
        Assert.Equal("Exact", result.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    // ApplyTo(MapStructureRegistrationTuning) — 非 null 覆盖
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ApplyTo_StructureTuning_OnlyOverridesNonNullFields()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 3.0,
            MinimumEdgeCoverage = 0.50,
            MinimumOccupancyCoverage = 0.40,
            EdgeDistanceTolerancePixels = 4.0,
            FastCoarseMaxDimension = 200,
            FastCoarseDownsampleFactor = 4,
            ScaleSearchRadius = 0.03,
            ScaleSearchStep = 0.02,
            MinimumCandidateMargin = 0.05
        };

        var profile = new ResolutionTuningProfile
        {
            MaximumChamferPixels = 4.5,        // 运行时硬锁 3.0，忽略覆盖
            MinimumEdgeCoverage = null,         // 不覆盖
            FastCoarseMaxDimension = 160,       // 应覆盖
            MinimumCandidateMargin = null       // 不覆盖
        };

        profile.ApplyTo(tuning);

        Assert.Equal(3.0, tuning.MaximumChamferPixels);
        Assert.Equal(0.50, tuning.MinimumEdgeCoverage);     // 未变
        Assert.Equal(0.40, tuning.MinimumOccupancyCoverage); // 未变
        Assert.Equal(4.0, tuning.EdgeDistanceTolerancePixels); // 未变
        Assert.Equal(160, tuning.FastCoarseMaxDimension);
        Assert.Equal(4, tuning.FastCoarseDownsampleFactor);   // 未变
        Assert.Equal(0.03, tuning.ScaleSearchRadius);         // 未变
        Assert.Equal(0.02, tuning.ScaleSearchStep);           // 未变
        Assert.Equal(0.05, tuning.MinimumCandidateMargin);    // 未变
    }

    [Fact]
    public void ApplyTo_StructureTuning_AllFieldsCanBeOverridden()
    {
        var tuning = new MapStructureRegistrationTuning
        {
            MaximumChamferPixels = 3.0,
            MinimumEdgeCoverage = 0.50,
            MinimumOccupancyCoverage = 0.40,
            EdgeDistanceTolerancePixels = 4.0,
            FastCoarseMaxDimension = 200,
            FastCoarseDownsampleFactor = 4,
            ScaleSearchRadius = 0.03,
            ScaleSearchStep = 0.02,
            MinimumCandidateMargin = 0.05
        };

        var profile = new ResolutionTuningProfile
        {
            MaximumChamferPixels = 4.5,
            MinimumEdgeCoverage = 0.30,
            MinimumOccupancyCoverage = 0.25,
            EdgeDistanceTolerancePixels = 3.5,
            FastCoarseMaxDimension = 160,
            FastCoarseDownsampleFactor = 2,
            ScaleSearchRadius = 0.05,
            ScaleSearchStep = 0.005,
            MinimumCandidateMargin = 0.08
        };

        profile.ApplyTo(tuning);

        Assert.Equal(3.0, tuning.MaximumChamferPixels);
        Assert.Equal(0.30, tuning.MinimumEdgeCoverage);
        Assert.Equal(0.25, tuning.MinimumOccupancyCoverage);
        Assert.Equal(3.5, tuning.EdgeDistanceTolerancePixels);
        Assert.Equal(160, tuning.FastCoarseMaxDimension);
        Assert.Equal(2, tuning.FastCoarseDownsampleFactor);
        Assert.Equal(0.05, tuning.ScaleSearchRadius);
        Assert.Equal(0.005, tuning.ScaleSearchStep);
        Assert.Equal(0.08, tuning.MinimumCandidateMargin);
    }

    // ═══════════════════════════════════════════════════════════════
    // ApplyTo(MapRecognitionTuning) — 非 null 覆盖
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ApplyTo_RecognitionTuning_OnlyOverridesNonNullFields()
    {
        var tuning = new MapRecognitionTuning
        {
            GateTemplateThreshold = 0.75,
            VectorErrorTolerance = 0.05
        };

        var profile = new ResolutionTuningProfile
        {
            GateTemplateThreshold = 0.65,   // 应覆盖
            VectorErrorTolerance = null      // 不覆盖
        };

        profile.ApplyTo(tuning);

        Assert.Equal(0.65, tuning.GateTemplateThreshold);
        Assert.Equal(0.05, tuning.VectorErrorTolerance); // 未变
    }

    [Fact]
    public void ApplyTo_RecognitionTuning_BothFieldsCanBeOverridden()
    {
        var tuning = new MapRecognitionTuning
        {
            GateTemplateThreshold = 0.75,
            VectorErrorTolerance = 0.05
        };

        var profile = new ResolutionTuningProfile
        {
            GateTemplateThreshold = 0.65,
            VectorErrorTolerance = 0.04
        };

        profile.ApplyTo(tuning);

        Assert.Equal(0.65, tuning.GateTemplateThreshold);
        Assert.Equal(0.04, tuning.VectorErrorTolerance);
    }

    // ═══════════════════════════════════════════════════════════════
    // 默认档案注入
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_FirstRun_InjectsDefaultProfiles()
    {
        var settings = new MapRuntimeSettings
        {
            SchemaVersion = MapRuntimeSettings.CurrentSchemaVersion
        };

        settings.Normalize();

        Assert.NotNull(settings.ResolutionTuningProfiles);
        Assert.Equal(3, settings.ResolutionTuningProfiles.Count);
        Assert.Contains(
            settings.ResolutionTuningProfiles,
            p => p.Name == "2560×1600 @ 120 DPI");
        Assert.Contains(
            settings.ResolutionTuningProfiles,
            p => p.Name == "1920×1080 @ 120 DPI");
        Assert.Contains(
            settings.ResolutionTuningProfiles,
            p => p.Name == "2560×1440 @ 120 DPI");
    }

    [Fact]
    public void Normalize_ExistingProfiles_NotOverwritten()
    {
        var settings = new MapRuntimeSettings
        {
            SchemaVersion = MapRuntimeSettings.CurrentSchemaVersion,
            ResolutionTuningProfiles =
            [
                new ResolutionTuningProfile
                {
                    Name = "Custom", ClientWidth = 1280, ClientHeight = 720, Dpi = 96
                }
            ]
        };

        settings.Normalize();

        Assert.Single(settings.ResolutionTuningProfiles);
        Assert.Equal("Custom", settings.ResolutionTuningProfiles[0].Name);
    }

    [Fact]
    public void Normalize_Default2560x1600Profile_HasExpectedOverrides()
    {
        var settings = new MapRuntimeSettings();
        settings.Normalize();

        var profile = settings.ResolutionTuningProfiles
            .Single(p => p.Name == "2560×1600 @ 120 DPI");

        Assert.Equal(2560, profile.ClientWidth);
        Assert.Equal(1600, profile.ClientHeight);
        Assert.Equal(120, profile.Dpi);
        Assert.Equal(0.55d, profile.MinimumEdgeCoverage);
        Assert.Equal(0.08d, profile.MinimumCandidateMargin);
        Assert.Equal(0.04d, profile.VectorErrorTolerance);
        // 未设置的字段应为 null
        Assert.Null(profile.MaximumChamferPixels);
        Assert.Null(profile.FastCoarseMaxDimension);
    }

    [Fact]
    public void Normalize_Default1920x1080Profile_HasExpectedOverrides()
    {
        var settings = new MapRuntimeSettings();
        settings.Normalize();

        var profile = settings.ResolutionTuningProfiles
            .Single(p => p.Name == "1920×1080 @ 120 DPI");

        Assert.Equal(3.0d, profile.MaximumChamferPixels);
        Assert.Equal(0.30d, profile.MinimumEdgeCoverage);
        Assert.Equal(3.5d, profile.EdgeDistanceTolerancePixels);
        Assert.Equal(180, profile.FastCoarseMaxDimension);
        Assert.Equal(2, profile.FastCoarseDownsampleFactor);
        Assert.Equal(0.04d, profile.ScaleSearchRadius);
        Assert.Equal(0.03d, profile.MinimumCandidateMargin);
    }

    // ═══════════════════════════════════════════════════════════════
    // Clone() — 深度拷贝
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Clone_ProfilesAreDeepCopied()
    {
        var settings = new MapRuntimeSettings
        {
            ResolutionTuningProfiles =
            [
                new ResolutionTuningProfile
                {
                    Name = "Original",
                    ClientWidth = 1920,
                    ClientHeight = 1080,
                    Dpi = 120,
                    MinimumEdgeCoverage = 0.30
                }
            ]
        };

        var clone = settings.Clone();

        Assert.Single(clone.ResolutionTuningProfiles);
        Assert.Equal("Original", clone.ResolutionTuningProfiles[0].Name);

        // 修改 clone 不影响原对象
        clone.ResolutionTuningProfiles[0].Name = "Modified";
        clone.ResolutionTuningProfiles[0].MinimumEdgeCoverage = 0.99;

        Assert.Equal("Original", settings.ResolutionTuningProfiles[0].Name);
        Assert.Equal(0.30, settings.ResolutionTuningProfiles[0].MinimumEdgeCoverage);
    }

    [Fact]
    public void Clone_EmptyProfilesList_RemainsIndependent()
    {
        var settings = new MapRuntimeSettings
        {
            ResolutionTuningProfiles = []
        };

        var clone = settings.Clone();

        Assert.NotNull(clone.ResolutionTuningProfiles);
        Assert.Empty(clone.ResolutionTuningProfiles);

        clone.ResolutionTuningProfiles.Add(new ResolutionTuningProfile());
        Assert.Empty(settings.ResolutionTuningProfiles);
    }
}
