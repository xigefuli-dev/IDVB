using System.Text.Json;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class ZongcaiNativeIdvaTests
{
    private static string PackagePath => Path.Combine(AppContext.BaseDirectory,
        "Assets", "Algorithms", "structure-zongcai-native-reference-v1.idva");

    [Fact]
    public async Task CleanReferenceKeepsDarkCorridorsAndSmallInteriorGeometry()
    {
        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(PackagePath);
        using var source = new Mat(300, 400, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(source, new Rect(60, 60, 260, 160), new Scalar(79, 66, 61), -1);
        Cv2.Rectangle(source, new Rect(130, 110, 25, 25), Scalar.Black, -1);
        using var edges = engine.Execute(algorithm, source);
        using var corridorRim = new Mat(edges, new Rect(57, 57, 269, 7));
        using var innerRim = new Mat(edges, new Rect(126, 106, 33, 33));
        using var background = new Mat(edges, new Rect(0, 0, 40, 300));
        Assert.True(Cv2.CountNonZero(corridorRim) > 200);
        Assert.True(Cv2.CountNonZero(innerRim) > 100);
        Assert.Equal(0, Cv2.CountNonZero(background));
        using var live = Vpsg3FastLiveExtractor.Extract(source);
        Assert.Equal(0, live.EdgePixelCount);
    }

    [LocalCatalogFact]
    public async Task LocalZongcaiCatalogMatchesAdaptedNativeContourGeometry()
    {
        var root = Environment.GetEnvironmentVariable("IDVB_ZONGCAI_MAPS")!;
        var output = Environment.GetEnvironmentVariable("IDVB_ZONGCAI_OUTPUT")!;
        Directory.CreateDirectory(output);
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "maps.json")));
        var maps = catalog.RootElement.GetProperty("Maps").Deserialize<MapRecord[]>()!
            .Where(m => m.Class.Contains("总裁", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(maps);
        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(PackagePath);
        var rows = new List<object>();
        foreach (var map in maps)
        foreach (var floor in map.Floors)
        {
            var path = Path.Combine(root, map.Id.ToString("N"), floor.RecognitionFileName!);
            using var source = Cv2.ImRead(path, ImreadModes.Unchanged);
            using var native = NativeCleanReference(source);
            using var actual = engine.Execute(algorithm, source);
            var difference = Cv2.Norm(native, actual, NormTypes.INF);
            Assert.Equal(0, difference);
            var file = $"{map.Id:N}-{floor.Key}.png";
            Assert.True(Cv2.ImWrite(Path.Combine(output, file), actual));
            rows.Add(new { map.Id, map.SequenceNumber, floor.Key, width = actual.Width,
                height = actual.Height, edgePixels = Cv2.CountNonZero(actual), maxPixelDifference = difference });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "parity.json"), JsonSerializer.Serialize(rows));
    }

    // Independent adaptation of the native contour stages. The four intentional
    // reference-only differences are documented in the delivered package.
    private static Mat NativeCleanReference(Mat source)
    {
        using var bgr = MapBackgroundProcessor.CompositeToBgr(source);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var room = new Mat();
        using var wrap = new Mat();
        using var corridor = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 18, 1), new Scalar(25, 165, 200), room);
        Cv2.InRange(hsv, new Scalar(170, 18, 1), new Scalar(179, 165, 200), wrap);
        Cv2.BitwiseOr(room, wrap, room);
        Cv2.InRange(hsv, new Scalar(95, 14, 1), new Scalar(130, 105, 200), corridor);
        using var open = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using var close = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        var contours = new List<Point[]>();
        foreach (var mask in new[] { room, corridor })
        {
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, open);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, close);
            Cv2.FindContours(mask, out Point[][] found, out _, RetrievalModes.CComp,
                ContourApproximationModes.ApproxSimple);
            contours.AddRange(found.Where(c => Cv2.ArcLength(c, true) >= 30)
                .Select(c => Cv2.ApproxPolyDP(c, .55, true)));
        }
        var result = new Mat(source.Size(), MatType.CV_8UC1, Scalar.Black);
        if (contours.Count > 0) Cv2.DrawContours(result, contours, -1, Scalar.White, 2, LineTypes.Link8);
        return result;
    }

    public sealed class LocalCatalogFactAttribute : FactAttribute
    {
        public LocalCatalogFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("IDVB_ZONGCAI_MAPS") is null
                || Environment.GetEnvironmentVariable("IDVB_ZONGCAI_OUTPUT") is null)
                Skip = "Requires local Zongcai catalog and diagnostic output path.";
        }
    }
}
