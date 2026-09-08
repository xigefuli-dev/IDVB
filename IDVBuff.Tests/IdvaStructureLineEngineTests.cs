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

    [Fact]
    public async Task WangQingxinDedicatedAlgorithmLoadsAndExecutesSuccessfully()
    {
        var algorithmPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Algorithms",
            "structure-doom-girl-hard-wangqingxin-v1.idva");
        if (!File.Exists(algorithmPath))
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            algorithmPath = Path.Combine(repoRoot, "Assets", "Algorithms", "structure-doom-girl-hard-wangqingxin-v1.idva");
        }
        Assert.True(File.Exists(algorithmPath), $"Algorithm file not found: {algorithmPath}");

        var engine = new IdvaStructureLineEngine();
        var algorithm = await engine.LoadAsync(algorithmPath);
        Assert.Equal("structure.doom-girl.hard.wangqingxin.v1", algorithm.AlgorithmId);
        Assert.Equal("S0 厄运之女困难专属结构线图（王清心）", algorithm.DisplayName);
        Assert.Equal("1.1", algorithm.SchemaVersion);

        // 创建模拟底图：暗蓝灰背景、暖褐房间、蓝灰走廊，且走廊中央横跨一条粗红路线
        using var source = new Mat(200, 260, MatType.CV_8UC3, new Scalar(42, 36, 26)); // 暗底
        Cv2.Rectangle(source, new Rect(30, 30, 80, 80), new Scalar(77, 89, 101), -1); // 暖褐房间
        Cv2.Rectangle(source, new Rect(110, 50, 120, 40), new Scalar(95, 83, 72), -1); // 蓝灰走廊
        Cv2.Line(source, new Point(110, 70), new Point(230, 70), new Scalar(30, 30, 220), 11); // 粗红路线

        using var edges = engine.Execute(algorithm, source);
        Assert.Equal(source.Size(), edges.Size());
        Assert.True(Cv2.CountNonZero(edges) > 50);

        // 验证红线内部没有被掏空为双轨（在走廊内部红线中心线上采样）
        using var centerLine = new Mat(edges, new Rect(130, 70, 80, 1));
        Assert.Equal(0, Cv2.CountNonZero(centerLine));

        // Bright floors and navy voids must not be mistaken for walls or blue annotations.
        using var brightFloor = new Mat(240, 320, MatType.CV_8UC3, new Scalar(42, 36, 26));
        Cv2.Rectangle(brightFloor, new Rect(20, 20, 280, 200), new Scalar(135, 135, 135), -1);
        Cv2.Rectangle(brightFloor, new Rect(70, 60, 45, 45), HsvToBgr(106, 100, 90), -1);
        Cv2.Line(brightFloor, new Point(180, 45), new Point(180, 115), new Scalar(175, 175, 175), 3);
        Cv2.Line(brightFloor, new Point(45, 170), new Point(150, 170), HsvToBgr(115, 230, 180), 5);
        using var brightEdges = engine.Execute(algorithm, brightFloor);
        using var floorBorder = new Mat(brightEdges, new Rect(15, 15, 290, 12));
        using var voidBorder = new Mat(brightEdges, new Rect(65, 55, 55, 55));
        using var wall = new Mat(brightEdges, new Rect(174, 40, 13, 80));
        using var blueRouteInterior = new Mat(brightEdges, new Rect(50, 162, 90, 17));
        Assert.True(Cv2.CountNonZero(floorBorder) > 200);
        Assert.True(Cv2.CountNonZero(voidBorder) > 100);
        Assert.True(Cv2.CountNonZero(wall) > 50);
        Assert.Equal(0, Cv2.CountNonZero(blueRouteInterior));
    }

    [Fact]
    public async Task ApproxPolyDpBackwardCompatibilityAndSimplification()
    {
        var root = Path.Combine(Path.GetTempPath(), $"idva-approx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var defaultAlgorithmPath = Path.Combine(root, "default.idva");
            var approxAlgorithmPath = Path.Combine(root, "approx.idva");
            var testImagePath = Path.Combine(root, "test.png");

            await File.WriteAllTextAsync(defaultAlgorithmPath, Algorithm);
            var approxAlgorithmDsl = Algorithm.Replace(
                @"""stage"":""contours"",""retrieval"":""RETR_LIST"",""chain"":""CHAIN_APPROX_SIMPLE""",
                @"""stage"":""contours"",""retrieval"":""RETR_LIST"",""chain"":""CHAIN_APPROX_SIMPLE"",""approx_poly_dp_epsilon"":2.0");
            await File.WriteAllTextAsync(approxAlgorithmPath, approxAlgorithmDsl);

            using var image = CreateRoom();
            // 在矩形边缘加几个微小的 1 像素噪点毛刺
            image.Set(25, 20, new Vec3b(120, 100, 15));
            image.Set(50, 20, new Vec3b(120, 100, 15));
            Cv2.ImWrite(testImagePath, image);

            var engine = new IdvaStructureLineEngine();
            var defaultAlgo = await engine.LoadAsync(defaultAlgorithmPath);
            var approxAlgo = await engine.LoadAsync(approxAlgorithmPath);

            using var defaultEdges = engine.Execute(defaultAlgo, image);
            using var approxEdges = engine.Execute(approxAlgo, image);

            Assert.Equal(image.Size(), defaultEdges.Size());
            Assert.Equal(image.Size(), approxEdges.Size());
            Assert.True(Cv2.CountNonZero(defaultEdges) > 0);
            Assert.True(Cv2.CountNonZero(approxEdges) > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OrthogonalSnapAlignsNearStraightLines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"idva-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var snapAlgorithmPath = Path.Combine(root, "snap.idva");
            var snapAlgorithmDsl = Algorithm.Replace(
                @"""stage"":""contours"",""retrieval"":""RETR_LIST"",""chain"":""CHAIN_APPROX_SIMPLE""",
                @"""stage"":""contours"",""retrieval"":""RETR_LIST"",""chain"":""CHAIN_APPROX_SIMPLE"",""approx_poly_dp_epsilon"":2.0,""orthogonal_snap_px"":3");
            await File.WriteAllTextAsync(snapAlgorithmPath, snapAlgorithmDsl);

            using var image = CreateRoom();
            var engine = new IdvaStructureLineEngine();
            var snapAlgo = await engine.LoadAsync(snapAlgorithmPath);

            using var edges = engine.Execute(snapAlgo, image);
            Assert.Equal(image.Size(), edges.Size());
            Assert.True(Cv2.CountNonZero(edges) > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GenerateWangQingxinLocalPrebuiltStructureLines()
    {
        var mapsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB",
            "Maps");
        if (!Directory.Exists(mapsDirectory) || !File.Exists(Path.Combine(mapsDirectory, "maps.json")))
            return;

        var repo = new MapRepository(mapsDirectory);
        var catalog = await repo.GetCatalogSnapshotAsync();
        var targetClass = catalog.Classes.FirstOrDefault(c => c.Contains("困难"));
        Assert.NotNull(targetClass);

        var algorithmPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Algorithms",
            "structure-doom-girl-hard-wangqingxin-v1.idva");
        if (!File.Exists(algorithmPath))
        {
            var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
            algorithmPath = Path.Combine(repoRoot, "Assets", "Algorithms", "structure-doom-girl-hard-wangqingxin-v1.idva");
        }

        var result = await repo.GeneratePrebuiltStructureLinesAsync(targetClass, algorithmPath);
        Assert.Equal(29, result.MapCount);
        Assert.Equal(58, result.FloorCount);
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
