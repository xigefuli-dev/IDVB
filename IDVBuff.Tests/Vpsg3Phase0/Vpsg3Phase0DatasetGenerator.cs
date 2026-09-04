using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests.Vpsg3Phase0;

public static class Vpsg3Phase0DatasetGenerator
{
    public static List<GroundTruthSample> GenerateDataset()
    {
        var dataset = new List<GroundTruthSample>();

        // 1. Synthetic Reference from standard scenario geometry
        var (synRefColor, synRefLine) = BuildSyntheticReference(800, 600);

        // 2. Real Map Reference (if available in AppData)
        Mat? realRefColor = null;
        Mat? realRefLine = null;
        var appDataMaps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IDVB",
            "Maps");

        var sampleMapDir = Path.Combine(appDataMaps, "00d73bbbc9a442d2aabd4750c7944f74");
        if (Directory.Exists(sampleMapDir))
        {
            var realColorPath = Path.Combine(sampleMapDir, "floor-1-recognition.png");
            if (!File.Exists(realColorPath))
            {
                realColorPath = Path.Combine(sampleMapDir, "floor-1.png");
            }
            var realLinePath = Path.Combine(sampleMapDir, "prebuilt-1f.png");
            if (File.Exists(realColorPath) && File.Exists(realLinePath))
            {
                realRefColor = Cv2.ImRead(realColorPath, ImreadModes.Color);
                realRefLine = Cv2.ImRead(realLinePath, ImreadModes.Grayscale);
            }
        }

        // Test parameter matrix:
        // Scales: 0.82, 1.00, 1.18, 1.3375
        // Fog fractions: 0.0 (clean), 0.35 (mild fog), 0.60 (medium fog), 0.75 (severe fog)
        // Crop positions: Center, TopLeft shift, BottomRight shift
        var scales = new[] { 0.85d, 1.00d, 1.18d, 1.3375d };
        var fogLevels = new[] { 0.0d, 0.35d, 0.60d, 0.75d };
        var cropRatios = new[]
        {
            (0.15d, 0.15d), // Top-left biased crop
            (0.25d, 0.25d), // Center crop
            (0.35d, 0.30d)  // Bottom-right biased crop
        };

        var sampleIndex = 0;

        // Generate synthetic samples
        foreach (var scale in scales)
        {
            foreach (var fog in fogLevels)
            {
                foreach (var (rx, ry) in cropRatios)
                {
                    var sample = CreateSample(
                        $"syn_{sampleIndex++:D3}_s{scale:F2}_f{(int)(fog * 100)}",
                        "Synthetic",
                        "SyntheticGroundTruthMap",
                        "1F",
                        synRefColor,
                        synRefLine,
                        scale,
                        rx,
                        ry,
                        fog,
                        hasDynamicExclusion: (sampleIndex % 2 == 0));

                    dataset.Add(sample);
                }
            }
        }

        // Generate real map samples if available
        if (realRefColor is not null && realRefLine is not null && !realRefColor.Empty() && !realRefLine.Empty())
        {
            var realScales = new[] { 0.88d, 1.00d, 1.25d };
            var realFogs = new[] { 0.0d, 0.40d, 0.70d };
            foreach (var scale in realScales)
            {
                foreach (var fog in realFogs)
                {
                    var sample = CreateSample(
                        $"real_{sampleIndex++:D3}_s{scale:F2}_f{(int)(fog * 100)}",
                        "RealMap",
                        "RealMap_00d73bbb",
                        "1F",
                        realRefColor,
                        realRefLine,
                        scale,
                        0.22d,
                        0.22d,
                        fog,
                        hasDynamicExclusion: true);

                    dataset.Add(sample);
                }
            }
        }

        synRefColor.Dispose();
        synRefLine.Dispose();
        realRefColor?.Dispose();
        realRefLine?.Dispose();

        return dataset;
    }

    private static GroundTruthSample CreateSample(
        string id,
        string sourceType,
        string refName,
        string floorKey,
        Mat refColor,
        Mat refLine,
        double scale,
        double cropRatioX,
        double cropRatioY,
        double fogFraction,
        bool hasDynamicExclusion)
    {
        var scaledW = (int)Math.Round(refColor.Width * scale);
        var scaledH = (int)Math.Round(refColor.Height * scale);

        using var scaledColor = new Mat();
        Cv2.Resize(refColor, scaledColor, new Size(scaledW, scaledH), interpolation: InterpolationFlags.Linear);

        // Crop viewport window (e.g. 520x400)
        var cropW = Math.Min(scaledW - 10, (int)Math.Round(refColor.Width * 0.65 * scale));
        var cropH = Math.Min(scaledH - 10, (int)Math.Round(refColor.Height * 0.65 * scale));
        var cropX = Math.Clamp((int)Math.Round(cropRatioX * (scaledW - cropW)), 0, scaledW - cropW);
        var cropY = Math.Clamp((int)Math.Round(cropRatioY * (scaledH - cropH)), 0, scaledH - cropH);

        var cropRect = new Rect(cropX, cropY, cropW, cropH);
        var liveImage = new Mat(scaledColor, cropRect).Clone();

        // Compute ground-truth visible structural edges in live viewport coordinates
        using var scaledLine = new Mat();
        Cv2.Resize(refLine, scaledLine, new Size(scaledW, scaledH), interpolation: InterpolationFlags.Nearest);
        var liveGtLine = new Mat(scaledLine, cropRect).Clone();

        // Screen viewport bounds: simulating window placement
        const double vpScreenX = 240d;
        const double vpScreenY = 160d;
        var viewport = new MapScreenRect(vpScreenX, vpScreenY, cropW, cropH);

        // Ground Truth Canonical Offset:
        // offsetX = ViewportBounds.X - cropX
        // offsetY = ViewportBounds.Y - cropY
        var trueOffsetX = vpScreenX - cropX;
        var trueOffsetY = vpScreenY - cropY;

        // Apply Fog if requested
        if (fogFraction > 0.01d)
        {
            ApplyFogOfWar(liveImage, fogFraction);

            var w = liveGtLine.Width;
            var h = liveGtLine.Height;
            var visibleRadius = (int)(Math.Min(w, h) * (1.0 - fogFraction * 0.75));
            using var fogMask = new Mat(liveGtLine.Size(), MatType.CV_8UC1, Scalar.Black);
            Cv2.Circle(fogMask, new Point(w / 2, h / 2), visibleRadius, Scalar.White, thickness: -1);
            Cv2.BitwiseAnd(liveGtLine, fogMask, liveGtLine);
        }

        // Apply Dynamic Exclusion artifacts (HUD / Glyphs)
        if (hasDynamicExclusion)
        {
            ApplyHudExclusionArtifacts(liveImage);

            var w = liveGtLine.Width;
            var h = liveGtLine.Height;
            var greenRect = new Rect(0, (int)(h * 0.75), (int)(w * 0.22), (int)(h * 0.25));
            Cv2.Rectangle(liveGtLine, greenRect, Scalar.Black, thickness: -1);
            var glyphY = (int)(h * 0.05);
            var glyphW = (int)(w * 0.70);
            Cv2.Rectangle(liveGtLine, new Rect((int)(w * 0.10), glyphY, glyphW, 30), Scalar.Black, thickness: -1);
        }

        return new GroundTruthSample(
            Id: id,
            SourceType: sourceType,
            ReferenceName: refName,
            FloorKey: floorKey,
            LiveImage: liveImage,
            ReferenceStructureLine: refLine.Clone(),
            GroundTruthVisibleEdge: liveGtLine,
            TrueScale: scale,
            TrueOffsetX: trueOffsetX,
            TrueOffsetY: trueOffsetY,
            ViewportBounds: viewport,
            QueryBounds: new Rect(0, 0, cropW, cropH),
            FogFraction: fogFraction,
            HasDynamicExclusion: hasDynamicExclusion,
            IsAmbiguous: false);
    }

    private static void ApplyFogOfWar(Mat image, double fogFraction)
    {
        // Simulate game fog: visible circle/ellipse around player, remainder shrouded
        var w = image.Width;
        var h = image.Height;
        var visibleRadius = (int)(Math.Min(w, h) * (1.0 - fogFraction * 0.75));

        using var fogMask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black);
        var center = new Point(w / 2, h / 2);
        Cv2.Circle(fogMask, center, visibleRadius, Scalar.White, thickness: -1);
        Cv2.GaussianBlur(fogMask, fogMask, new Size(21, 21), 7.0);

        // Blend image with black background according to fogMask
        using var gray = new Mat();
        using var fog3 = new Mat();
        Cv2.CvtColor(fogMask, fog3, ColorConversionCodes.GRAY2BGR);
        
        using var floatImg = new Mat();
        using var floatFog = new Mat();
        image.ConvertTo(floatImg, MatType.CV_32FC3, 1.0 / 255.0);
        fog3.ConvertTo(floatFog, MatType.CV_32FC3, 1.0 / 255.0);

        using var blended = new Mat();
        Cv2.Multiply(floatImg, floatFog, blended);
        blended.ConvertTo(image, MatType.CV_8UC3, 255.0);
    }

    private static void ApplyHudExclusionArtifacts(Mat image)
    {
        var w = image.Width;
        var h = image.Height;

        // Bottom-left green HUD
        var greenRect = new Rect(0, (int)(h * 0.75), (int)(w * 0.22), (int)(h * 0.25));
        Cv2.Rectangle(image, greenRect, new Scalar(40, 180, 50), thickness: -1);

        // Top objective white glyphs
        var glyphY = (int)(h * 0.05);
        for (var i = 0; i < 4; i++)
        {
            var gx = (int)(w * 0.20 + i * (w * 0.12));
            Cv2.Rectangle(image, new Rect(gx, glyphY, 24, 24), Scalar.White, thickness: -1);
        }
    }

    public static (Mat Color, Mat Line) BuildSyntheticReference(int width, int height)
    {
        var color = new Mat(new Size(width, height), MatType.CV_8UC3, Scalar.Black);
        var line = new Mat(new Size(width, height), MatType.CV_8UC1, Scalar.Black);

        void AddWall(int x, int y, int w, int h)
        {
            var rect = new Rect(x, y, w, h);
            Cv2.Rectangle(color, rect, new Scalar(120, 120, 160), thickness: -1);
            Cv2.Rectangle(line, rect, Scalar.White, thickness: 2);
        }

        // Outer boundary walls
        AddWall(40, 40, width - 80, 16);
        AddWall(40, height - 56, width - 80, 16);
        AddWall(40, 40, 16, height - 80);
        AddWall(width - 56, 40, 16, height - 80);

        // Interior room dividers & corridors
        AddWall(180, 40, 16, 260);
        AddWall(180, 360, 16, height - 400);
        AddWall(340, 160, 240, 16);
        AddWall(420, 320, 16, 180);
        AddWall(560, 260, 160, 16);

        return (color, line);
    }
}
