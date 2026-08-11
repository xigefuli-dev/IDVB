using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CompleteAlignmentTestCollection
{
    public const string Name = "Complete alignment integration";
}

internal enum VisibleGates
{
    None,
    MainOnly,
    SideOnly,
    Both
}

/// <summary>
/// Self-contained integration fixture for the complete alignment suites.
/// It persists a real three-floor map, lets the repository generate all
/// derived assets, and drives the production recognition service with the
/// shipped gate template.
/// </summary>
internal sealed class CompleteAlignmentTestScenario : IAsyncDisposable
{
    public const string MainFloor = "main";
    public const string UpperFloor = "upper";
    public const string BasementFloor = "basement";

    private readonly Mat _mainImage;
    private readonly Mat _upperImage;
    private readonly Mat _basementImage;
    private readonly Rect _mainGateBounds;
    private readonly Rect _sideGateBounds;

    private CompleteAlignmentTestScenario(
        string root,
        MapRecord map,
        MapCvRecognitionService service,
        Mat mainImage,
        Mat upperImage,
        Mat basementImage,
        Rect mainGateBounds,
        Rect sideGateBounds)
    {
        Root = root;
        Map = map;
        Service = service;
        _mainImage = mainImage;
        _upperImage = upperImage;
        _basementImage = basementImage;
        _mainGateBounds = mainGateBounds;
        _sideGateBounds = sideGateBounds;
    }

    public string Root { get; }
    public MapRecord Map { get; }
    public MapCvRecognitionService Service { get; }

    public static MapRecognitionTuning RecognitionTuning => new()
    {
        GateTemplateThreshold = 0.70d,
        MinimumConfidence = 0.30d,
        VectorErrorTolerance = 0.06d,
        AmbiguityMargin = 0.01d,
        ConfirmationAdvantage = 0.01d,
        ForceBestRecognitionResult = true,
        ForceCandidateSelection = false
    };

    public static MapStructureRegistrationTuning StructureTuning
    {
        get
        {
            var tuning = new MapStructureRegistrationTuning
            {
                UseAuxiliaryAnchorRecognition = false,
                EnableFastAlignment = false,
                StructureFallbackBudgetMilliseconds = 5_000,
                MinimumEdgePixels = 50,
                MinimumSpanPixels = 18,
                MinimumConsistentPartitions = 2,
                TopCandidateCount = 6,
                MaximumChamferPixels = 3.5d,
                MinimumEdgeCoverage = 0.50d,
                MinimumOccupancyCoverage = 0.35d,
                MinimumCandidateMargin = 0.025d,
                ScaleSearchRadius = 0.04d,
                ScaleSearchStep = 0.01d,
                EnableEccRefinement = true,
                EnableFeatureVoting = true
            };
            tuning.Normalize();
            return tuning;
        }
    }

    public static async Task<CompleteAlignmentTestScenario> CreateAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"IDVBuff.CompleteAlignment.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        Mat? mainImage = null;
        Mat? upperImage = null;
        Mat? basementImage = null;
        MapCvRecognitionService? service = null;
        try
        {
            mainImage = BuildStructuredReference(800, 600, variant: 0);
            upperImage = BuildStructuredReference(720, 540, variant: 1);
            basementImage = BuildStructuredReference(680, 520, variant: 2);

            var gatePath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "Gate.png");
            using var gate = Cv2.ImRead(gatePath, ImreadModes.Color);
            if (gate.Empty())
                throw new InvalidOperationException($"Gate template missing: {gatePath}");

            const double gateScale = 0.275d;
            var mainGate = PasteGate(mainImage, gate, 118, 478, gateScale);
            var sideGate = PasteGate(mainImage, gate, 646, 82, gateScale);

            var mainPath = Path.Combine(root, "main.png");
            var upperPath = Path.Combine(root, "upper.png");
            var basementPath = Path.Combine(root, "basement.png");
            if (!Cv2.ImWrite(mainPath, mainImage)
                || !Cv2.ImWrite(upperPath, upperImage)
                || !Cv2.ImWrite(basementPath, basementImage))
            {
                throw new InvalidOperationException("Failed to persist synthetic map images.");
            }

            var recognition = BuildRecognition(
                mainImage.Size(),
                upperImage.Size(),
                basementImage.Size(),
                mainGate,
                sideGate);
            var sideFeaturePath = Path.Combine(root, "side-feature.png");
            var sideProfile = recognition.FirstFloor;
            using (var sideFeature = new SideEntranceFeaturePreprocessor().Process(
                mainImage,
                sideProfile.FindAnchor("side-entrance")!.Bounds!,
                featureRadius: 48))
            {
                if (!Cv2.ImWrite(sideFeaturePath, sideFeature.Feature))
                    throw new InvalidOperationException("Failed to persist side feature.");
                sideProfile.SideEntranceFeatureCenterX = sideFeature.CenterX;
                sideProfile.SideEntranceFeatureCenterY = sideFeature.CenterY;
                sideProfile.SideEntranceFeatureRadius = sideFeature.Radius;
            }
            var repository = new MapRepository(Path.Combine(root, "maps"));
            var map = await repository.SaveAsync(
                new MapDraft
                {
                    Title = "Complete alignment synthetic map",
                    Floors =
                    [
                        new FloorDefinition
                        {
                            Key = MainFloor,
                            DisplayName = "Main",
                            SortOrder = 1
                        },
                        new FloorDefinition
                        {
                            Key = UpperFloor,
                            DisplayName = "Upper",
                            SortOrder = 2
                        },
                        new FloorDefinition
                        {
                            Key = BasementFloor,
                            DisplayName = "Basement",
                            SortOrder = 3
                        }
                    ],
                    FloorPaths = new Dictionary<string, string>
                    {
                        [MainFloor] = mainPath,
                        [UpperFloor] = upperPath,
                        [BasementFloor] = basementPath
                    },
                    SideEntranceFeaturePaths = new Dictionary<string, string>
                    {
                        [MainFloor] = sideFeaturePath,
                        // Deliberately provide a non-primary feature too. The
                        // initial side scan must filter it at the operation
                        // boundary and return only the map's primary floor.
                        [UpperFloor] = sideFeaturePath
                    },
                    Recognition = recognition
                },
                sideEntranceFeatureRadius: 48);

            service = new MapCvRecognitionService(repository);
            await service.RefreshCacheAsync();
            return new CompleteAlignmentTestScenario(
                root,
                map,
                service,
                mainImage,
                upperImage,
                basementImage,
                mainGate,
                sideGate);
        }
        catch
        {
            service?.Dispose();
            mainImage?.Dispose();
            upperImage?.Dispose();
            basementImage?.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            throw;
        }
    }

    public CapturedGameFrame MainFrame(
        VisibleGates gates,
        MapScreenRect? viewport = null,
        Rect? crop = null)
    {
        using var full = _mainImage.Clone();
        if (gates is VisibleGates.None or VisibleGates.SideOnly)
            EraseGate(full, _mainGateBounds);
        if (gates is VisibleGates.None or VisibleGates.MainOnly)
            EraseGate(full, _sideGateBounds);

        var image = crop is { } region
            ? new Mat(full, region).Clone()
            : full.Clone();
        var bounds = viewport ?? new MapScreenRect(
            100d,
            80d,
            image.Width,
            image.Height);
        return new CapturedGameFrame(
            image,
            DisplayTestMatrix.Baseline.PhysicalBounds,
            bounds,
            IntPtr.Zero);
    }

    public CapturedGameFrame FloorFrame(
        string floorKey,
        Rect crop,
        MapScreenRect viewport)
    {
        var source = floorKey switch
        {
            UpperFloor => _upperImage,
            BasementFloor => _basementImage,
            _ => throw new ArgumentOutOfRangeException(nameof(floorKey))
        };
        return new CapturedGameFrame(
            new Mat(source, crop).Clone(),
            DisplayTestMatrix.Baseline.PhysicalBounds,
            viewport,
            IntPtr.Zero);
    }

    /// <summary>
    /// 把楼层参考图按给定比例整体缩放后作为 live 帧，用于构造
    /// "真实 scale ≠ 跨楼层 seed scale" 的 VPSG 引导场景。
    /// </summary>
    public CapturedGameFrame FloorFrameScaled(
        string floorKey,
        double scale,
        MapScreenRect viewport)
    {
        var source = floorKey switch
        {
            UpperFloor => _upperImage,
            BasementFloor => _basementImage,
            _ => throw new ArgumentOutOfRangeException(nameof(floorKey))
        };
        using var resized = new Mat();
        Cv2.Resize(
            source,
            resized,
            new Size(
                (int)Math.Round(source.Width * scale),
                (int)Math.Round(source.Height * scale)),
            interpolation: InterpolationFlags.Linear);
        return new CapturedGameFrame(
            resized.Clone(),
            DisplayTestMatrix.Baseline.PhysicalBounds,
            viewport,
            IntPtr.Zero);
    }

    public MapOverlayTransform FloorScaleSeed(string floorKey)
    {
        var profile = MapFloorRules.GetFloorProfile(Map, floorKey)
            ?? throw new InvalidOperationException($"Missing floor profile: {floorKey}");
        return new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            ReferenceWidth = profile.RecognitionPixelWidth,
            ReferenceHeight = profile.RecognitionPixelHeight,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
    }

    public async ValueTask DisposeAsync()
    {
        Service.Dispose();
        _mainImage.Dispose();
        _upperImage.Dispose();
        _basementImage.Dispose();
        await Task.Yield();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private static MapRecognitionProfile BuildRecognition(
        Size mainSize,
        Size upperSize,
        Size basementSize,
        Rect mainGate,
        Rect sideGate)
    {
        var recognition = new MapRecognitionProfile();
        recognition.EnsureStandardAnchors();
        recognition.FirstFloor.FloorKey = MainFloor;
        recognition.FirstFloor.RecognitionPixelWidth = mainSize.Width;
        recognition.FirstFloor.RecognitionPixelHeight = mainSize.Height;
        recognition.FirstFloor.FindAnchor("main-entrance")!.Bounds =
            Normalize(mainGate, mainSize);
        recognition.FirstFloor.FindAnchor("side-entrance")!.Bounds =
            Normalize(sideGate, mainSize);

        recognition.SecondFloor.FloorKey = UpperFloor;
        recognition.SecondFloor.RecognitionPixelWidth = upperSize.Width;
        recognition.SecondFloor.RecognitionPixelHeight = upperSize.Height;
        var basement = new FloorRecognitionProfile
        {
            FloorKey = BasementFloor,
            RecognitionPixelWidth = basementSize.Width,
            RecognitionPixelHeight = basementSize.Height
        };
        recognition.Floors = new Dictionary<string, FloorRecognitionProfile>
        {
            [MainFloor] = recognition.FirstFloor,
            [UpperFloor] = recognition.SecondFloor,
            [BasementFloor] = basement
        };
        return recognition;
    }

    private static NormalizedRectangle Normalize(Rect rect, Size size) => new()
    {
        X = (double)rect.X / size.Width,
        Y = (double)rect.Y / size.Height,
        Width = (double)rect.Width / size.Width,
        Height = (double)rect.Height / size.Height
    };

    private static Rect PasteGate(
        Mat target,
        Mat gate,
        int x,
        int y,
        double scale)
    {
        using var resized = new Mat();
        Cv2.Resize(
            gate,
            resized,
            new Size(),
            scale,
            scale,
            InterpolationFlags.Linear);
        var bounds = new Rect(x, y, resized.Width, resized.Height);
        using var destination = new Mat(target, bounds);
        resized.CopyTo(destination);
        return bounds;
    }

    private static void EraseGate(Mat image, Rect bounds)
    {
        var padded = new Rect(
            Math.Max(0, bounds.X - 2),
            Math.Max(0, bounds.Y - 2),
            Math.Min(image.Width - Math.Max(0, bounds.X - 2), bounds.Width + 4),
            Math.Min(image.Height - Math.Max(0, bounds.Y - 2), bounds.Height + 4));
        Cv2.Rectangle(image, padded, Scalar.Black, thickness: -1);
    }

    private static Mat BuildStructuredReference(int width, int height, int variant)
    {
        var image = new Mat(new Size(width, height), MatType.CV_8UC3, Scalar.Black);
        void Box(double x, double y, double w, double h, int gray = 255) =>
            Cv2.Rectangle(
                image,
                new Rect(
                    (int)Math.Round(x * width),
                    (int)Math.Round(y * height),
                    (int)Math.Round(w * width),
                    (int)Math.Round(h * height)),
                Scalar.All(gray),
                thickness: -1);

        Box(0.05, 0.06, 0.17, 0.18);
        Box(0.31, 0.04, 0.23, 0.13);
        Box(0.69, 0.11, 0.16, 0.24);
        Box(0.10, 0.43, 0.16, 0.29);
        Box(0.36, 0.34, 0.20, 0.25);
        Box(0.66, 0.57, 0.22, 0.20);
        Box(0.31, 0.72, 0.13, 0.15, 190);
        Cv2.Line(
            image,
            new Point((int)(0.20d * width), (int)(0.14d * height)),
            new Point((int)(0.34d * width), (int)(0.10d * height)),
            Scalar.White,
            12 + variant * 2);
        Cv2.Line(
            image,
            new Point((int)(0.49d * width), (int)(0.16d * height)),
            new Point((int)(0.47d * width), (int)(0.36d * height)),
            Scalar.White,
            10 + variant * 2);
        Cv2.Line(
            image,
            new Point((int)(0.25d * width), (int)(0.55d * height)),
            new Point((int)(0.38d * width), (int)(0.48d * height)),
            Scalar.White,
            11 + variant);
        Cv2.Line(
            image,
            new Point((int)(0.56d * width), (int)(0.49d * height)),
            new Point((int)(0.69d * width), (int)(0.67d * height)),
            Scalar.White,
            9 + variant);
        Cv2.Circle(
            image,
            new Point((int)(0.46d * width), (int)(0.47d * height)),
            18 + variant * 3,
            Scalar.Black,
            thickness: -1);
        return image;
    }
}
