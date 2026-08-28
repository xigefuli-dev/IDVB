using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IDVBuff.Tests;
public sealed partial class MapMultiFloorAlignmentTests
{

    private static Task AssertFloorAlignmentAsync(
        MapCvRecognitionService service,
        Guid mapId,
        string floorKey,
        Mat reference,
        Rect crop)
    {
        using var live = new Mat(reference, crop).Clone();
        var viewport = new MapScreenRect(700d, 360d, live.Width, live.Height);
        using var frame = new CapturedGameFrame(
            live.Clone(),
            new MapScreenRect(0d, 0d, 1920d, 1080d),
            viewport,
            IntPtr.Zero);
        var tuning = new MapStructureRegistrationTuning
        {
            MinimumEdgePixels = 50,
            MinimumSpanPixels = 18,
            MinimumConsistentPartitions = 2,
            TopCandidateCount = 6,
            MaximumChamferPixels = 3.5d,
            MinimumEdgeCoverage = 0.50d,
            MinimumOccupancyCoverage = 0.35d,
            MinimumCandidateMargin = 0.025d,
            ScaleSearchRadius = 0.15d,
            ScaleSearchStep = 0.01d
        };
        var seed = new MapOverlayTransform
        {
            ScaleX = 1d,
            ScaleY = 1d,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceWidth = reference.Width,
            ReferenceHeight = reference.Height,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };

        var attempt = service.AlignFloorWithoutGates(
            frame,
            mapId,
            floorKey,
            seed,
            MapOverlayAlignmentMode.Uniform,
            new MapRecognitionTuning { MinimumConfidence = 0.30d },
            tuning);

        Assert.NotNull(attempt.Recognition);
        Assert.Equal(floorKey, attempt.Recognition.Result.Floor);
        Assert.Equal(0, attempt.Diagnostics.GateCandidateCount);
        Assert.InRange(
            Math.Abs(attempt.Recognition.Result.OverlayTransform!.OffsetX
                - (viewport.X - crop.X)),
            0d,
            4d);
        Assert.InRange(
            Math.Abs(attempt.Recognition.Result.OverlayTransform.OffsetY
                - (viewport.Y - crop.Y)),
            0d,
            4d);
        return Task.CompletedTask;
    }

    private static Mat BuildStructuredReference(int width, int height, int variant)
    {
        var image = new Mat(new Size(width, height), MatType.CV_8UC3, Scalar.Black);
        void Box(double x, double y, double w, double h) => Cv2.Rectangle(
            image,
            new Rect(
                (int)(x * width),
                (int)(y * height),
                (int)(w * width),
                (int)(h * height)),
            Scalar.White,
            -1);
        Box(0.07, 0.08, 0.19, 0.19);
        Box(0.38, 0.06, 0.25, 0.16);
        Box(0.73, 0.14, 0.14, 0.28);
        Box(0.14, 0.50, 0.17, 0.31);
        Box(0.44, 0.41, 0.20, 0.28);
        Box(0.72, 0.66, 0.20, 0.20);
        Cv2.Line(
            image,
            new Point((int)(0.25 * width), (int)(0.18 * height)),
            new Point((int)(0.40 * width), (int)(0.14 * height)),
            Scalar.White,
            14 + variant * 2);
        Cv2.Line(
            image,
            new Point((int)(0.58 * width), (int)(0.22 * height)),
            new Point((int)(0.55 * width), (int)(0.44 * height)),
            Scalar.White,
            12 + variant * 2);
        Cv2.Circle(
            image,
            new Point((int)(0.54 * width), (int)(0.57 * height)),
            18 + variant * 2,
            Scalar.Black,
            -1);
        return image;
    }

    private static MapFloorScaleCalibration Calibration(
        string floorKey,
        Guid mapId,
        DateTimeOffset updatedAt) => new()
    {
        MapId = mapId,
        MapUpdatedAt = updatedAt,
        PrimaryFloorKey = "primary",
        FloorKey = floorKey
    };
}
