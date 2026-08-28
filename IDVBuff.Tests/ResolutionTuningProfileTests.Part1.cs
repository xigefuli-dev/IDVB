using IDVBuff.Features.Maps;
using System.Text.Json;

namespace IDVBuff.Tests;
public sealed partial class ResolutionTuningProfileTests
{

    // ═══════════════════════════════════════════════════════════════
    // JSON 序列化往返
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RoundTrip_ProfileWithAllFields_RestoresCorrectly()
    {
        var profile = new ResolutionTuningProfile
        {
            Name = "Test",
            ClientWidth = 2560,
            ClientHeight = 1440,
            Dpi = 120,
            MatchTolerancePixels = 150,
            MaximumChamferPixels = 4.0,
            MinimumEdgeCoverage = 0.35,
            MinimumOccupancyCoverage = 0.25,
            EdgeDistanceTolerancePixels = 3.0,
            FastCoarseMaxDimension = 180,
            FastCoarseDownsampleFactor = 3,
            ScaleSearchRadius = 0.06,
            ScaleSearchStep = 0.02,
            MinimumCandidateMargin = 0.04,
            GateTemplateThreshold = 0.72,
            VectorErrorTolerance = 0.03
        };

        var json = JsonSerializer.Serialize(profile);
        var restored = JsonSerializer.Deserialize<ResolutionTuningProfile>(json)!;

        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal(profile.ClientWidth, restored.ClientWidth);
        Assert.Equal(profile.ClientHeight, restored.ClientHeight);
        Assert.Equal(profile.Dpi, restored.Dpi);
        Assert.Equal(profile.MatchTolerancePixels, restored.MatchTolerancePixels);
        Assert.Equal(profile.MaximumChamferPixels, restored.MaximumChamferPixels);
        Assert.Equal(profile.MinimumEdgeCoverage, restored.MinimumEdgeCoverage);
        Assert.Equal(profile.MinimumOccupancyCoverage, restored.MinimumOccupancyCoverage);
        Assert.Equal(profile.EdgeDistanceTolerancePixels, restored.EdgeDistanceTolerancePixels);
        Assert.Equal(profile.FastCoarseMaxDimension, restored.FastCoarseMaxDimension);
        Assert.Equal(profile.FastCoarseDownsampleFactor, restored.FastCoarseDownsampleFactor);
        Assert.Equal(profile.ScaleSearchRadius, restored.ScaleSearchRadius);
        Assert.Equal(profile.ScaleSearchStep, restored.ScaleSearchStep);
        Assert.Equal(profile.MinimumCandidateMargin, restored.MinimumCandidateMargin);
        Assert.Equal(profile.GateTemplateThreshold, restored.GateTemplateThreshold);
        Assert.Equal(profile.VectorErrorTolerance, restored.VectorErrorTolerance);
    }

    [Fact]
    public void RoundTrip_SettingsWithProfiles_PreservesAllProfiles()
    {
        var settings = new MapRuntimeSettings
        {
            ResolutionTuningProfiles =
            [
                new ResolutionTuningProfile
                {
                    Name = "A", ClientWidth = 1920, ClientHeight = 1080, Dpi = 96,
                    MaximumChamferPixels = 5.0
                },
                new ResolutionTuningProfile
                {
                    Name = "B", ClientWidth = 2560, ClientHeight = 1440, Dpi = 120,
                    GateTemplateThreshold = 0.68
                }
            ]
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<MapRuntimeSettings>(json)!;
        restored.Normalize();

        Assert.Equal(2, restored.ResolutionTuningProfiles.Count);
        Assert.Contains(restored.ResolutionTuningProfiles, p => p.Name == "A");
        Assert.Contains(restored.ResolutionTuningProfiles, p => p.Name == "B");
        Assert.Equal(
            5.0,
            restored.ResolutionTuningProfiles.Single(p => p.Name == "A")
                .MaximumChamferPixels);
        Assert.Equal(
            0.68,
            restored.ResolutionTuningProfiles.Single(p => p.Name == "B")
                .GateTemplateThreshold);
    }
}
