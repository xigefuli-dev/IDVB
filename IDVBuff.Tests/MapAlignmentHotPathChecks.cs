using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Features.Maps;
using OpenCvSharp;
using Xunit.Abstractions;

namespace IDVBuff.Tests;

public sealed class MapAlignmentHotPathChecks(ITestOutputHelper output)
{
    [Fact]
    public void ReplayNativeExtractionOnRealFrames()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_REAL_REPLAY_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../scratch/native-replay"));
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.GetFiles(root, "viewport.png", SearchOption.AllDirectories))
        {
            using var image = Cv2.ImRead(path);
            var start = Stopwatch.GetTimestamp();
            using var before = IdvaNativeObservedExtractorBaseline.Process(image);
            var oldMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            start = Stopwatch.GetTimestamp();
            using var after = IdvaNativeObservedExtractor.Process(image);
            var newMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(0, Cv2.Norm(before.ObservedEdges, after.ObservedEdges, NormTypes.INF));
            Assert.Equal(0, Cv2.Norm(before.ValidMask, after.ValidMask, NormTypes.INF));
            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(path)!).Replace(Path.DirectorySeparatorChar, '_');
            Cv2.ImWrite(Path.Combine(destination, relative + ".png"), after.ObservedEdges);
            output.WriteLine($"{relative}: identical edges+mask; old={oldMs:F2}ms new={newMs:F2}ms");
        }
    }

    [Fact]
    public void DynamicCompositionPreservesPremultipliedPixels()
    {
        using var background = new Bitmap(2560, 1600, PixelFormat.Format32bppPArgb);
        background.SetResolution(144, 144);
        using (var g = Graphics.FromImage(background)) g.Clear(Color.FromArgb(117, 203, 81, 37));
        var scene = new MapOverlayRenderScene(2560, 1600, 144, null, null, false);
        var oldTimes = new List<double>();
        var newTimes = new List<double>();
        for (var i = 0; i < 8; i++)
        {
            var start = Stopwatch.GetTimestamp();
            using var old = new Bitmap(background);
            oldTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            start = Stopwatch.GetTimestamp();
            using var composed = MapOverlayBitmapRenderer.ComposeDynamic(background, scene);
            newTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            Assert.Equal(PixelFormat.Format32bppPArgb, composed.PixelFormat);
            Assert.Equal(background.GetPixel(1200, 800), composed.GetPixel(1200, 800));
            Assert.Equal(background.HorizontalResolution, composed.HorizontalResolution);
        }
        output.WriteLine($"2560x1600 copy median: Bitmap(Image)={oldTimes.Order().ElementAt(4):F3}ms; pixel clone+compose={newTimes.Order().ElementAt(4):F3}ms");
    }

    [Fact]
    public void NativeObservationIsFrameOwnedAndDoesNotEscapeDisposal()
    {
        using var pixels = new Mat(120, 180, MatType.CV_8UC3, Scalar.Black);
        var frame = new CapturedGameFrame(pixels.Clone(), default, new(0, 0, 180, 120), IntPtr.Zero);
        var first = frame.GetOrCreateNativeObservedStructure();
        Assert.Same(first, frame.GetOrCreateNativeObservedStructure());
        using (var independent = MapStructurePreprocessor.UseNativeObservedStructureLine(first.ObservedEdges, first.ValidMask))
            Assert.Equal(first.ObservedEdges.Size(), independent.Edges.Size());
        Assert.False(first.ObservedEdges.IsDisposed);
        frame.Dispose();
        Assert.True(first.ObservedEdges.IsDisposed);
        Assert.True(first.ValidMask.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => frame.GetOrCreateNativeObservedStructure());
    }
}
