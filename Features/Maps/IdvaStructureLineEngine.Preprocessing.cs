using OpenCvSharp;
using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class IdvaStructureLineEngine
{
    private static void ApplyToneAdjustment(PipelineState state, JsonElement stage)
    {
        var gamma = stage.TryGetProperty("gamma", out _) ? ReadBoundedDouble(stage, "gamma", 0.1, 5.0) : 1.0;
        var satScale = stage.TryGetProperty("saturation_scale", out _) ? ReadBoundedDouble(stage, "saturation_scale", 0.0, 5.0) : 1.0;

        var current = state.Bgr;
        Mat? intermediate = null;

        if (Math.Abs(gamma - 1.0) > 0.001)
        {
            var lutData = new byte[256];
            var invGamma = 1.0 / gamma;
            for (var i = 0; i < 256; i++)
            {
                var v = Math.Pow(i / 255.0, invGamma) * 255.0;
                lutData[i] = (byte)Math.Clamp(Math.Round(v), 0, 255);
            }
            using var lut = Mat.FromPixelData(1, 256, MatType.CV_8UC1, lutData);
            var gammaCorrected = new Mat();
            Cv2.LUT(current, lut, gammaCorrected);
            intermediate = gammaCorrected;
            current = gammaCorrected;
        }

        if (Math.Abs(satScale - 1.0) > 0.001)
        {
            using var hsv = new Mat();
            Cv2.CvtColor(current, hsv, ColorConversionCodes.BGR2HSV);
            var channels = Cv2.Split(hsv);
            var sLutData = new byte[256];
            for (var i = 0; i < 256; i++)
            {
                sLutData[i] = (byte)Math.Clamp(Math.Round(i * satScale), 0, 255);
            }
            using var sLut = Mat.FromPixelData(1, 256, MatType.CV_8UC1, sLutData);
            using var sNew = new Mat();
            Cv2.LUT(channels[1], sLut, sNew);
            channels[1].Dispose();
            channels[1] = sNew;

            using var mergedHsv = new Mat();
            Cv2.Merge(channels, mergedHsv);
            channels[0].Dispose();
            channels[2].Dispose();

            var satAdjusted = new Mat();
            Cv2.CvtColor(mergedHsv, satAdjusted, ColorConversionCodes.HSV2BGR);
            intermediate?.Dispose();
            state.ReplaceBgr(satAdjusted);
            return;
        }

        if (intermediate != null)
        {
            state.ReplaceBgr(intermediate);
        }
    }

    private static void ApplyClaheContrast(PipelineState state, JsonElement stage)
    {
        var clipLimit = stage.TryGetProperty("clip_limit", out _) ? ReadBoundedDouble(stage, "clip_limit", 0.1, 40.0) : 2.0;
        var tileSize = ReadGridSize(stage, "tile_grid_size");

        using var lab = new Mat();
        Cv2.CvtColor(state.Bgr, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        using var clahe = Cv2.CreateCLAHE(clipLimit, tileSize);
        using var clahedL = new Mat();
        clahe.Apply(channels[0], clahedL);
        channels[0].Dispose();
        channels[0] = clahedL;

        using var mergedLab = new Mat();
        Cv2.Merge(channels, mergedLab);
        channels[1].Dispose();
        channels[2].Dispose();

        var result = new Mat();
        Cv2.CvtColor(mergedLab, result, ColorConversionCodes.Lab2BGR);
        state.ReplaceBgr(result);
    }

    private static Size ReadGridSize(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return new Size(8, 8);
        var arr = prop.EnumerateArray().Select(v => v.TryGetInt32(out var n) ? n : 8).ToArray();
        if (arr.Length != 2 || arr[0] <= 0 || arr[1] <= 0)
            return new Size(8, 8);
        return new Size(arr[0], arr[1]);
    }

    private static void ApplyUnsharpMask(PipelineState state, JsonElement stage)
    {
        var amount = stage.TryGetProperty("amount", out _) ? ReadBoundedDouble(stage, "amount", 0.0, 10.0) : 1.0;
        var radius = stage.TryGetProperty("radius", out _) ? ReadBoundedDouble(stage, "radius", 0.1, 20.0) : 1.5;

        var ksize = (int)Math.Ceiling(radius * 3) * 2 + 1;
        using var blurred = new Mat();
        Cv2.GaussianBlur(state.Bgr, blurred, new Size(ksize, ksize), radius);
        var sharpened = new Mat();
        Cv2.AddWeighted(state.Bgr, 1.0 + amount, blurred, -amount, 0, sharpened);
        state.ReplaceBgr(sharpened);
    }

    private static void ApplySeparateClasses(PipelineState state, JsonElement stage)
    {
        var gapPx = stage.TryGetProperty("gap_px", out _) ? ReadBoundedInt(stage, "gap_px", 1, 31) : 3;
        var shrinkTarget = stage.TryGetProperty("shrink_target", out var st) ? st.GetString() : "corridor";

        if (shrinkTarget is not ("corridor" or "room"))
            shrinkTarget = "corridor";

        var k = gapPx * 2 + 1;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));

        if (shrinkTarget == "corridor")
        {
            using var dilatedRoom = new Mat();
            Cv2.Dilate(state.Room, dilatedRoom, kernel);
            using var notDilated = new Mat();
            Cv2.BitwiseNot(dilatedRoom, notDilated);
            var separatedCorr = new Mat();
            Cv2.BitwiseAnd(state.Corridor, notDilated, separatedCorr);
            state.ReplaceCorridor(separatedCorr);
        }
        else
        {
            using var dilatedCorr = new Mat();
            Cv2.Dilate(state.Corridor, dilatedCorr, kernel);
            using var notDilated = new Mat();
            Cv2.BitwiseNot(dilatedCorr, notDilated);
            var separatedRoom = new Mat();
            Cv2.BitwiseAnd(state.Room, notDilated, separatedRoom);
            state.ReplaceRoom(separatedRoom);
        }
    }

    private static void ApplyWallBarriers(PipelineState state, JsonElement stage)
    {
        var minBrightness = stage.TryGetProperty("wall_min_brightness", out _)
            ? ReadBoundedInt(stage, "wall_min_brightness", 0, 255)
            : 108;
        var maxSaturation = stage.TryGetProperty("wall_max_saturation", out _)
            ? ReadBoundedInt(stage, "wall_max_saturation", 0, 255)
            : 55;
        var excludeRoom = !stage.TryGetProperty("exclude_room", out _) || ReadBoolean(stage, "exclude_room");
        var excludeRoutes = stage.TryGetProperty("exclude_routes", out _) && ReadBoolean(stage, "exclude_routes");
        var minContrast = stage.TryGetProperty("wall_min_contrast", out _)
            ? ReadBoundedInt(stage, "wall_min_contrast", 0, 255)
            : 0;

        using var gray = new Mat();
        Cv2.CvtColor(state.Bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var barrier = new Mat();
        Cv2.Threshold(gray, barrier, minBrightness, 255, ThresholdTypes.Binary);

        using var hsv = new Mat();
        Cv2.CvtColor(state.Bgr, hsv, ColorConversionCodes.BGR2HSV);
        var hsvChannels = Cv2.Split(hsv);
        using var lowSatMask = new Mat();
        Cv2.Threshold(hsvChannels[1], lowSatMask, maxSaturation, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(barrier, lowSatMask, barrier);
        for (var i = 0; i < hsvChannels.Length; i++)
            hsvChannels[i].Dispose();

        if (minContrast > 0)
        {
            var size = stage.TryGetProperty("wall_contrast_kernel", out _)
                ? ReadSize(stage, "wall_contrast_kernel") : new Size(9, 9);
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, size);
            using var contrast = new Mat();
            Cv2.MorphologyEx(gray, contrast, MorphTypes.TopHat, kernel);
            Cv2.Threshold(contrast, contrast, minContrast, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(barrier, contrast, barrier);
        }
        if (excludeRoutes && state.RouteMask is not null)
            barrier.SetTo(Scalar.Black, state.RouteMask);

        if (excludeRoom)
        {
            using var notRoom = new Mat();
            Cv2.BitwiseNot(state.Room, notRoom);
            Cv2.BitwiseAnd(barrier, notRoom, barrier);
        }

        using var notBarrier = new Mat();
        Cv2.BitwiseNot(barrier, notBarrier);

        var separatedCorridor = new Mat();
        Cv2.BitwiseAnd(state.Corridor, notBarrier, separatedCorridor);
        state.ReplaceCorridor(separatedCorridor);
    }

    private static Point[] SnapOrthogonal(Point[] points, int tolerancePx)
    {
        if (tolerancePx <= 0 || points.Length < 3)
            return points;

        var count = points.Length;
        var snapped = (Point[])points.Clone();
        for (var i = 0; i < count; i++)
        {
            var p1 = snapped[i];
            var nextIndex = (i + 1) % count;
            var p2 = snapped[nextIndex];
            var dx = Math.Abs(p1.X - p2.X);
            var dy = Math.Abs(p1.Y - p2.Y);

            if (dx <= tolerancePx && dy > dx * 2)
            {
                var avgX = (p1.X + p2.X + 1) / 2;
                snapped[i] = new Point(avgX, p1.Y);
                snapped[nextIndex] = new Point(avgX, p2.Y);
            }
            else if (dy <= tolerancePx && dx > dy * 2)
            {
                var avgY = (p1.Y + p2.Y + 1) / 2;
                snapped[i] = new Point(p1.X, avgY);
                snapped[nextIndex] = new Point(p2.X, avgY);
            }
        }
        return snapped;
    }
}
