using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class MapStructurePrebuiltRouteTests
{
    [Fact]
    public void PrebuiltLineIsUsedAsEdgesWithoutReferenceExtraction()
    {
        using var line = Mat.Zeros(16, 20, MatType.CV_8UC1).ToMat();
        line.Set(4, 7, (byte)255);

        using var features = MapStructurePreprocessor.UsePrebuiltStructureLine(line);

        Assert.Equal(255, features.Edges.At<byte>(4, 7));
        Assert.Equal(1, Cv2.CountNonZero(features.Edges));
        Assert.Empty(features.KeyPoints);
        Assert.True(features.Descriptors.Empty());
        Assert.Equal(
            MapStructurePreprocessingProfile.PrebuiltStructureLine,
            features.DiagnosticTiming?.Profile);
        Assert.True(features.DiagnosticTiming?.DescriptorExtractionSkipped);
    }

    [Fact]
    public void PrebuiltReferenceCacheKeepsOnlyTheCurrentCandidate()
    {
        using var cache = new MapStructureReferenceCache(
            new MapStructurePreprocessor());
        using var line = Mat.Zeros(16, 20, MatType.CV_8UC1).ToMat();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        using (cache.GetOrCreate(first, DateTimeOffset.UnixEpoch, line,
            profile: MapStructurePreprocessingProfile.PrebuiltStructureLine)) { }
        using (cache.GetOrCreate(second, DateTimeOffset.UnixEpoch, line,
            profile: MapStructurePreprocessingProfile.PrebuiltStructureLine)) { }

        Assert.Null(cache.TryRentResident(first, DateTimeOffset.UnixEpoch,
            profile: MapStructurePreprocessingProfile.PrebuiltStructureLine));
        using var current = cache.TryRentResident(second, DateTimeOffset.UnixEpoch,
            profile: MapStructurePreprocessingProfile.PrebuiltStructureLine);
        Assert.NotNull(current);
    }
}
