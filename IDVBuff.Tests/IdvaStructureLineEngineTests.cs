using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

public sealed class IdvaStructureLineEngineTests
{
    [Fact]
    public async Task RedAndGreenRouteOverlaysDoNotChangeTheStructureEdges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"idva-routes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var algorithmPath = Path.Combine(root, "normal.idva");
            var plainPath = Path.Combine(root, "plain.png");
            var routedPath = Path.Combine(root, "routed.png");
            var plainEdgesPath = Path.Combine(root, "plain-edges.png");
            var routedEdgesPath = Path.Combine(root, "routed-edges.png");
            await File.WriteAllTextAsync(algorithmPath, Algorithm);
            using (var plain = CreateRoom())
            using (var routed = plain.Clone())
            {
                Cv2.Line(routed, new Point(30, 70), new Point(170, 70), HsvToBgr(0, 230, 220), 7);
                Cv2.Line(routed, new Point(30, 100), new Point(170, 100), HsvToBgr(60, 230, 220), 7);
                Assert.True(Cv2.ImWrite(plainPath, plain));
                Assert.True(Cv2.ImWrite(routedPath, routed));
            }

            var engine = new IdvaStructureLineEngine();
            var algorithm = await engine.LoadAsync(algorithmPath);
            engine.Execute(algorithm, plainPath, plainEdgesPath);
            engine.Execute(algorithm, routedPath, routedEdgesPath);
            using var routedSource = Cv2.ImRead(routedPath, ImreadModes.Unchanged);
            using var inMemoryEdges = engine.Execute(algorithm, routedSource);
            using var plainEdges = Cv2.ImRead(plainEdgesPath, ImreadModes.Grayscale);
            using var routedEdges = Cv2.ImRead(routedEdgesPath, ImreadModes.Grayscale);
            Assert.Equal(0d, Cv2.Norm(plainEdges, routedEdges, NormTypes.L1));
            Assert.Equal(0d, Cv2.Norm(inMemoryEdges, routedEdges, NormTypes.L1));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeObservedExtractorProducesExpectedObservedEdgesAndValidMask()
    {
        using var source = new Mat(240, 320, MatType.CV_8UC3, new Scalar(45, 45, 45));
        Cv2.Rectangle(source, new Rect(50, 40, 210, 150), new Scalar(100, 110, 150), -1);
        Cv2.Rectangle(source, new Rect(50, 40, 210, 150), Scalar.White, 3);
        Cv2.Rectangle(source, new Rect(0, 205, 100, 35), new Scalar(100, 110, 150), -1);
        Cv2.Rectangle(source, new Rect(0, 205, 100, 35), Scalar.White, 3);

        using var result = IdvaNativeObservedExtractor.Process(source);
        Assert.Equal(source.Size(), result.ObservedEdges.Size());
        Assert.Equal(source.Size(), result.ValidMask.Size());
        Assert.True(Cv2.CountNonZero(result.ObservedEdges) > 100);
        Assert.Equal(255, result.ValidMask.At<byte>(40, 80));
        using var borderArtifact = new Mat(
            result.ObservedEdges,
            new Rect(0, 200, 110, 40));
        Assert.Equal(0, Cv2.CountNonZero(borderArtifact));
    }

    private static Mat CreateRoom()
    {
        var image = new Mat(160, 200, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(image, new Rect(20, 20, 160, 120), HsvToBgr(15, 100, 120), -1);
        return image;
    }

    private static Scalar HsvToBgr(byte hue, byte saturation, byte value)
    {
        using var hsv = new Mat(1, 1, MatType.CV_8UC3, new Scalar(hue, saturation, value));
        using var bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        var pixel = bgr.At<Vec3b>(0, 0);
        return new Scalar(pixel.Item0, pixel.Item1, pixel.Item2);
    }

    private const string Algorithm = """
        {"format":"IDVA","schema_version":"1.1","algorithm_id":"structure.normal.route-test","display_name":"Route test","profile_family":"structure-map","profile_style":"normal","geometry_policy":{"preserve_input_size":true,"allow_resize":false,"allow_rotation":false,"allow_warp":false},"runtime":{"engine":"idvb-opencv-pipeline","language":"declarative-json","minimum_engine_version":"1.0"},"input":{"type":"raster-image","color_order":"BGR"},"output":{"type":"binary-edge-map","background":0,"edge":255,"line_width_px":2},"pipeline":[{"stage":"color_classification","mode":"HSV_RANGE"},{"stage":"ignore_route_overlays","mode":"HSV_RANGES"},{"stage":"morph_open","kernel":[3,3]},{"stage":"morph_close","kernels":[[13,13]]},{"stage":"contours","retrieval":"RETR_LIST","chain":"CHAIN_APPROX_SIMPLE"},{"stage":"draw_edges","line_width_px":2,"antialias":false}],"parameters":{"room_hsv_lo":[7,35,55],"room_hsv_hi":[22,180,190],"corridor_hsv_lo":[0,0,55],"corridor_hsv_hi":[179,60,180],"route_hsv_ranges":[{"lo":[0,80,60],"hi":[10,255,255]},{"lo":[170,80,60],"hi":[179,255,255]},{"lo":[35,60,60],"hi":[100,255,255]}],"route_mask_dilate_kernel":[5,5],"route_repair_radius_px":5}}
        """;
}
