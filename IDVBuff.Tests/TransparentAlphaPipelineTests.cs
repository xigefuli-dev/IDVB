using OpenCvSharp;
using Xunit;
using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class TransparentAlphaPipelineTests
{
    [Fact]
    public void NormalizeTransparentPixels_ClearsRgbOnlyWhenAlphaIsZero()
    {
        using var mat = new Mat(2, 2, MatType.CV_8UC4);
        mat.Set(0, 0, new Vec4b(255, 255, 255, 0));
        mat.Set(0, 1, new Vec4b(12, 34, 56, 0));
        mat.Set(1, 0, new Vec4b(100, 150, 200, 255));
        mat.Set(1, 1, new Vec4b(100, 150, 200, 128));

        MapBackgroundProcessor.NormalizeTransparentPixels(mat);

        Assert.Equal(new Vec4b(0, 0, 0, 0), mat.At<Vec4b>(0, 0));
        Assert.Equal(new Vec4b(0, 0, 0, 0), mat.At<Vec4b>(0, 1));
        Assert.Equal(new Vec4b(100, 150, 200, 255), mat.At<Vec4b>(1, 0));
        Assert.Equal(new Vec4b(100, 150, 200, 128), mat.At<Vec4b>(1, 1));
    }

    [Fact]
    public void CompositeToBgr_CompositesOverBlackAndDropsAlpha()
    {
        using var mat = new Mat(2, 2, MatType.CV_8UC4);
        mat.Set(0, 0, new Vec4b(255, 255, 255, 0));
        mat.Set(0, 1, new Vec4b(100, 200, 50, 0));
        mat.Set(1, 0, new Vec4b(100, 150, 200, 255));
        mat.Set(1, 1, new Vec4b(100, 200, 50, 128));

        using var bgr = MapBackgroundProcessor.CompositeToBgr(mat);

        Assert.Equal(3, bgr.Channels());
        Assert.Equal(new Vec3b(0, 0, 0), bgr.At<Vec3b>(0, 0));
        Assert.Equal(new Vec3b(0, 0, 0), bgr.At<Vec3b>(0, 1));
        Assert.Equal(new Vec3b(100, 150, 200), bgr.At<Vec3b>(1, 0));

        var semi = bgr.At<Vec3b>(1, 1);
        Assert.InRange(semi.Item0, 48, 52);
        Assert.InRange(semi.Item1, 98, 102);
        Assert.InRange(semi.Item2, 23, 27);
    }

    [Fact]
    public async Task IdvaStructureLineEngine_ProducesIdenticalEdgesForDirtyAlphaBgraAndCleanBlackBgr()
    {
        var root = Path.Combine(Path.GetTempPath(), $"idva-alpha-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var algorithmPath = Path.Combine(root, "normal.idva");
            await File.WriteAllTextAsync(algorithmPath, IdvaStructureLineEngineTests.Algorithm);

            // Create clean BGR image with black background (100x100) and gray square inside (20..80)
            using var cleanBgr = new Mat(100, 100, MatType.CV_8UC3, Scalar.Black);
            Cv2.Rectangle(cleanBgr, new Rect(20, 20, 60, 60), new Scalar(100, 110, 150), -1);
            Cv2.Rectangle(cleanBgr, new Rect(20, 20, 60, 60), Scalar.White, 2);

            // Create dirty BGRA image with white / dirty texture under Alpha=0
            using var dirtyBgra = new Mat(100, 100, MatType.CV_8UC4, new Scalar(255, 255, 255, 0));
            for (int y = 20; y < 80; y++)
            {
                for (int x = 20; x < 80; x++)
                {
                    var p = cleanBgr.At<Vec3b>(y, x);
                    dirtyBgra.Set(y, x, new Vec4b(p.Item0, p.Item1, p.Item2, 255));
                }
            }

            var engine = new IdvaStructureLineEngine();
            var algorithm = await engine.LoadAsync(algorithmPath);

            using var cleanEdges = engine.Execute(algorithm, cleanBgr);
            using var dirtyEdges = engine.Execute(algorithm, dirtyBgra);

            // Dirty BGRA edges must match clean BGR edges exactly (0 difference)
            var diff = Cv2.Norm(cleanEdges, dirtyEdges, NormTypes.L1);
            Assert.Equal(0d, diff);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
