using IDVBuff.Core.Models;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class BackgroundScanTests
{
    [Fact]
    public void VerifiedBackgroundStructurePreservesContentScaleAndSidePrior()
    {
        var map = CreateMap();
        var sideSeed = CreateSideEntranceSeed(map, prior: 0.93d);
        var verified = new RuntimeMapRecognition
        {
            Map = map,
            FloorImagePath = "overlay.png",
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = "1f",
                Confidence = 0.9185d,
                IdentityConfidence = 0.9075d,
                LocalizationConfidence = 0.9185d,
                EvidenceKind = MapAlignmentEvidenceKind.Structure,
                StructureDisposition =
                    MapStructureEvidenceDisposition.Supportive,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 0.73602406768d,
                    ScaleY = 0.73602406768d,
                    OffsetX = 917d,
                    OffsetY = 44d,
                    ReferenceWidth = 200,
                    ReferenceHeight = 150,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            }
        };

        var result = BackgroundScanRules.BuildValidatedStructureScaleSeed(
            verified,
            sideSeed,
            "1f");

        Assert.NotNull(result);
        Assert.Equal(0.73602406768d, result!.LockedTransform.ScaleX, 10);
        Assert.Equal(0.93d, result.SideEntranceScanPriorConfidence);
        Assert.Equal("1f", result.FloorKey);
        Assert.Equal(map.UpdatedAt, result.MapUpdatedAt);
    }

    [Theory]
    [InlineData(MapAlignmentEvidenceKind.None,
        MapStructureEvidenceDisposition.Supportive, "1f")]
    [InlineData(MapAlignmentEvidenceKind.Structure,
        MapStructureEvidenceDisposition.Inconclusive, "1f")]
    [InlineData(MapAlignmentEvidenceKind.Structure,
        MapStructureEvidenceDisposition.Supportive, "b1f")]
    public void UnverifiedOrOtherFloorBackgroundResultCannotBecomeScaleSeed(
        MapAlignmentEvidenceKind evidence,
        MapStructureEvidenceDisposition disposition,
        string floor)
    {
        var map = CreateMap();
        var recognition = new RuntimeMapRecognition
        {
            Map = map,
            Result = new MapRecognitionResult
            {
                MapId = map.Id,
                Floor = floor,
                Confidence = 0.9d,
                LocalizationConfidence = 0.9d,
                EvidenceKind = evidence,
                StructureDisposition = disposition,
                OverlayTransform = new MapOverlayTransform
                {
                    ScaleX = 0.736d,
                    ScaleY = 0.736d,
                    ReferenceWidth = 200,
                    ReferenceHeight = 150,
                    AlignmentMode = MapOverlayAlignmentMode.Uniform
                }
            }
        };

        Assert.Null(BackgroundScanRules.BuildValidatedStructureScaleSeed(
            recognition,
            CreateSideEntranceSeed(map),
            "1f"));
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
