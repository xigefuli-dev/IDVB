using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed class MapArtworkLandmark
{
    public double SourceX { get; set; }
    public double SourceY { get; set; }
    public double ReferenceX { get; set; }
    public double ReferenceY { get; set; }

    public MapArtworkLandmark Clone() => (MapArtworkLandmark)MemberwiseClone();
}

public enum MapArtworkFitMethod
{
    LeastSquares = 0,
    MinimumMaximumError = 1
}

/// <summary>Maker-time, floor-local artwork pixels to reference pixels.</summary>
public sealed class MapArtworkRegistration
{
    public int SourceWidth { get; set; }
    public int SourceHeight { get; set; }
    public int ReferenceWidth { get; set; }
    public int ReferenceHeight { get; set; }
    public double[] SourceToReference { get; set; } = [1, 0, 0, 0, 1, 0];
    public List<MapArtworkLandmark> Landmarks { get; set; } = [];
    // Missing metadata preserves the fitting method used by existing packages.
    public MapArtworkFitMethod FitMethod { get; set; }
    // Author-sheet coordinates, independent of the canonical recognition crop.
    public NormalizedRectangle? SourceCropRegion { get; set; }
    public List<NormalizedPoint> SourceCropPoints { get; set; } = [];
    public bool SourceCropConfigured { get; set; }
    // New registrations clip only the aligned display to the complete canonical
    // footprint. Missing metadata retains existing packages' legacy display mode.
    public bool ClipToStructureFootprint { get; set; }

    public MapArtworkRegistration Clone() => new()
    {
        SourceWidth = SourceWidth,
        SourceHeight = SourceHeight,
        ReferenceWidth = ReferenceWidth,
        ReferenceHeight = ReferenceHeight,
        SourceToReference = (double[])SourceToReference.Clone(),
        Landmarks = Landmarks.Select(point => point.Clone()).ToList(),
        FitMethod = FitMethod,
        SourceCropRegion = SourceCropRegion?.Clone(),
        SourceCropPoints = SourceCropPoints.Select(point => point.Clone()).ToList(),
        SourceCropConfigured = SourceCropConfigured,
        ClipToStructureFootprint = ClipToStructureFootprint
    };

    public void SetSourceCrop(FloorRecognitionProfile? profile)
    {
        SourceCropRegion = profile?.RecognitionRegion?.Clone();
        SourceCropPoints = (profile?.FreeCropPoints ?? []).Select(point => point.Clone()).ToList();
        SourceCropConfigured = true;
        Validate();
    }

    public void Validate()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0 || ReferenceWidth <= 0 || ReferenceHeight <= 0
            || SourceToReference is not { Length: 6 }
            || SourceToReference.Any(value => !double.IsFinite(value)) || Landmarks is null
            || SourceCropPoints is null || !Enum.IsDefined(FitMethod))
            throw new InvalidOperationException("小抄与结构底图的尺寸或配准参数无效。");
        if (SourceCropRegion is { } region && (!region.IsValid
            || !double.IsFinite(region.X) || !double.IsFinite(region.Y)
            || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
            || region.X < 0 || region.Y < 0 || region.X + region.Width > 1.000001
            || region.Y + region.Height > 1.000001))
            throw new InvalidOperationException("小抄本层选区必须位于作者原图内。");
        if (SourceCropPoints.Count is 1 or 2 || SourceCropPoints.Any(point => point?.IsValid is not true))
            throw new InvalidOperationException("小抄本层裁切轮廓无效。");
        var matrix = SourceToReference;
        if (Math.Abs(matrix[0] * matrix[4] - matrix[1] * matrix[3]) < 1e-12)
            throw new InvalidOperationException("配准结果把地图压成了直线，请重新选择对应位置。");
        foreach (var point in Landmarks)
        {
            if (point is null || !double.IsFinite(point.SourceX) || !double.IsFinite(point.SourceY)
                || !double.IsFinite(point.ReferenceX) || !double.IsFinite(point.ReferenceY)
                || point.SourceX < 0 || point.SourceX >= SourceWidth
                || point.SourceY < 0 || point.SourceY >= SourceHeight
                || point.ReferenceX < 0 || point.ReferenceX >= ReferenceWidth
                || point.ReferenceY < 0 || point.ReferenceY >= ReferenceHeight)
                throw new InvalidOperationException("配准点必须位于各自楼层的图片内。");
        }
    }
}

public static class MapArtworkRegistrationService
{
    public static MapArtworkRegistration Fit(
        int sourceWidth, int sourceHeight, int referenceWidth, int referenceHeight,
        IReadOnlyList<MapArtworkLandmark> landmarks,
        MapArtworkFitMethod fitMethod = MapArtworkFitMethod.LeastSquares)
    {
        if (landmarks.Count < 3)
            throw new InvalidOperationException("请至少选择三组不在同一直线上的对应位置。");
        var registration = new MapArtworkRegistration
        {
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            ClipToStructureFootprint = true,
            FitMethod = fitMethod,
            Landmarks = landmarks.Select(point => point.Clone()).ToList()
        };
        registration.Validate();
        // Normalize each floor's coordinates for a well-conditioned solve.
        using var design = new Mat(landmarks.Count, 3, MatType.CV_64FC1);
        using var targets = new Mat(landmarks.Count, 2, MatType.CV_64FC1);
        for (var index = 0; index < landmarks.Count; index++)
        {
            var point = landmarks[index];
            design.Set(index, 0, point.SourceX / sourceWidth);
            design.Set(index, 1, point.SourceY / sourceHeight);
            design.Set(index, 2, 1d);
            targets.Set(index, 0, point.ReferenceX / referenceWidth);
            targets.Set(index, 1, point.ReferenceY / referenceHeight);
        }
        using var gram = new Mat();
        using var transpose = design.T();
        using var empty = new Mat();
        Cv2.Gemm(transpose, design, 1, empty, 0, gram);
        if (Math.Abs(Cv2.Determinant(gram)) < 1e-10)
            throw new InvalidOperationException("配准点集中在一条直线上，请选择分散的房间拐角。");
        using var coefficients = new Mat();
        if (!Cv2.Solve(design, targets, coefficients, DecompTypes.SVD))
            throw new InvalidOperationException("无法计算小抄配准，请检查对应点。");
        registration.SourceToReference =
        [
            coefficients.At<double>(0, 0) * referenceWidth / sourceWidth,
            coefficients.At<double>(1, 0) * referenceWidth / sourceHeight,
            coefficients.At<double>(2, 0) * referenceWidth,
            coefficients.At<double>(0, 1) * referenceHeight / sourceWidth,
            coefficients.At<double>(1, 1) * referenceHeight / sourceHeight,
            coefficients.At<double>(2, 1) * referenceHeight
        ];
        if (fitMethod == MapArtworkFitMethod.MinimumMaximumError)
            registration.SourceToReference = MapArtworkMinimaxSolver.Fit(
                sourceWidth, sourceHeight, referenceWidth, referenceHeight,
                landmarks, registration.SourceToReference);
        registration.Validate();
        return registration;
    }

    public static double[] EvaluateResiduals(
        MapArtworkRegistration registration, IReadOnlyList<MapArtworkLandmark> landmarks)
    {
        registration.Validate();
        var matrix = registration.SourceToReference;
        return landmarks.Select(point =>
        {
            var x = matrix[0] * point.SourceX + matrix[1] * point.SourceY + matrix[2];
            var y = matrix[3] * point.SourceX + matrix[4] * point.SourceY + matrix[5];
            return Math.Sqrt(Math.Pow(x - point.ReferenceX, 2) + Math.Pow(y - point.ReferenceY, 2));
        }).ToArray();
    }

    public static Mat BakeOverlay(Mat source, Mat canonical, MapArtworkRegistration registration)
    {
        if (registration.ClipToStructureFootprint)
        {
            using var registered = Bake(source, registration);
            return MapBackgroundProcessor.ClipToStructureFootprint(registered, canonical);
        }
        using var transparent = MapBackgroundProcessor.CreateWhiteKeyOverlay(source);
        return Bake(transparent, registration);
    }

    public static Mat Bake(Mat source, MapArtworkRegistration registration)
    {
        registration.Validate();
        if (source.Empty() || source.Width != registration.SourceWidth
            || source.Height != registration.SourceHeight || source.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("小抄图片已经变化，请重新对准结构底图。");
        using var rgba = new Mat();
        switch (source.Channels())
        {
            case 1: Cv2.CvtColor(source, rgba, ColorConversionCodes.GRAY2BGRA); break;
            case 3: Cv2.CvtColor(source, rgba, ColorConversionCodes.BGR2BGRA); break;
            case 4: source.CopyTo(rgba); break;
            default: throw new InvalidOperationException("小抄图片的颜色格式不受支持。");
        }
        if (registration.SourceCropRegion is not null || registration.SourceCropPoints.Count > 0)
        {
            using var cropMask = MapBackgroundProcessor.CreateFloorCropMask(rgba.Width, rgba.Height,
                registration.SourceCropRegion, registration.SourceCropPoints);
            rgba.SetTo(Scalar.All(0), cropMask);
        }
        using var affine = new Mat(2, 3, MatType.CV_64FC1);
        for (var index = 0; index < 6; index++)
            affine.Set(index / 3, index % 3, registration.SourceToReference[index]);
        var output = new Mat();
        try
        {
            Cv2.WarpAffine(rgba, output, affine,
                new Size(registration.ReferenceWidth, registration.ReferenceHeight),
                InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }
}
