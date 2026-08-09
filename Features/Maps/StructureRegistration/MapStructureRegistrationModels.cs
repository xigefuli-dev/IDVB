// Types split into:
//   MapStructureRegistrationModels.Enums.cs  – MapStructureRejectionReason, MapStructureEvidenceDisposition, MapStructureRejectionReasonExtensions
//   MapStructureRegistrationModels.Tuning.cs – MapStructureRegistrationTuning
//   MapStructureRegistrationModels.Request.cs – MapStructureRegistrationRequest
//   MapStructureRegistrationModels.Result.cs  – MapStructureCandidate, MapStructureRegistrationResult
//   MapStructureRegistrationModels.Detail.cs  – MapStructureConfidenceBreakdown, MapStructureConfidenceCalculator
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed class MapStructureFeatures : IDisposable
{
    public MapStructureFeatures(
        Mat nuisanceMask,
        Mat structureMask,
        Mat edges,
        Mat? referenceDistanceMap = null,
        Mat? clippedReferenceDistanceMap = null,
        double? clippedDistancePixels = null,
        Mat? normalizedGray = null,
        IReadOnlyList<Mat>? edgePyramid = null,
        KeyPoint[]? keyPoints = null,
        Mat? descriptors = null,
        Mat? repeatedRegionMask = null,
        PreprocessTiming? diagnosticTiming = null,
        Mat? rawVisibleMask = null)
    {
        NuisanceMask = nuisanceMask;
        StructureMask = structureMask;
        Edges = edges;
        ReferenceDistanceMap = referenceDistanceMap;
        ClippedReferenceDistanceMap = clippedReferenceDistanceMap;
        ClippedDistancePixels = clippedDistancePixels;
        NormalizedGray = normalizedGray ?? new Mat();
        EdgePyramid = edgePyramid ?? [];
        KeyPoints = keyPoints ?? [];
        Descriptors = descriptors ?? new Mat();
        RepeatedRegionMask = repeatedRegionMask ?? Mat.Zeros(
            edges.Size(),
            MatType.CV_8UC1).ToMat();
        DiagnosticTiming = diagnosticTiming;
        RawVisibleMask = rawVisibleMask;
    }

    public Mat NuisanceMask { get; }
    public Mat StructureMask { get; }
    public Mat Edges { get; }
    public Mat? ReferenceDistanceMap { get; private set; }
    public Mat? ClippedReferenceDistanceMap { get; private set; }
    public double? ClippedDistancePixels { get; private set; }
    public Mat NormalizedGray { get; }
    public IReadOnlyList<Mat> EdgePyramid { get; }
    public KeyPoint[] KeyPoints { get; }
    public Mat Descriptors { get; }
    public Mat RepeatedRegionMask { get; }
    public PreprocessTiming? DiagnosticTiming { get; }
    public Mat? RawVisibleMask { get; }

    /// <summary>按需创建匹配用的腐蚀掩码。调用者负责释放。</summary>
    public Mat? CreateSafeVisibleMask(int erodePixels = 1)
    {
        if (RawVisibleMask is null || RawVisibleMask.Empty())
            return null;
        var safe = new Mat();
        var kernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(1 + erodePixels * 2, 1 + erodePixels * 2));
        Cv2.Erode(RawVisibleMask, safe, kernel);
        return safe;
    }

    public Mat GetOrCreateReferenceDistanceMap()
    {
        if (ReferenceDistanceMap is { } existing && !existing.Empty())
            return existing;
        using var inverse = new Mat();
        Cv2.BitwiseNot(Edges, inverse);
        var distance = new Mat();
        Cv2.DistanceTransform(
            inverse,
            distance,
            DistanceTypes.L2,
            DistanceTransformMasks.Precise);
        ReferenceDistanceMap = distance;
        return distance;
    }

    public Mat GetOrCreateClippedReferenceDistanceMap(double clipPixels)
    {
        if (ClippedReferenceDistanceMap is { } existing
            && !existing.Empty()
            && ClippedDistancePixels is { } existingClip
            && Math.Abs(existingClip - clipPixels) < 0.0001d)
        {
            return existing;
        }
        ClippedReferenceDistanceMap?.Dispose();
        var distance = GetOrCreateReferenceDistanceMap().Clone();
        Cv2.Min(distance, clipPixels, distance);
        ClippedReferenceDistanceMap = distance;
        ClippedDistancePixels = clipPixels;
        return distance;
    }

    public MapStructureFeatures Clone() => new(
        NuisanceMask.Clone(),
        StructureMask.Clone(),
        Edges.Clone(),
        ReferenceDistanceMap?.Clone(),
        ClippedReferenceDistanceMap?.Clone(),
        ClippedDistancePixels,
        NormalizedGray.Clone(),
        EdgePyramid.Select(level => level.Clone()).ToArray(),
        KeyPoints.ToArray(),
        Descriptors.Clone(),
        RepeatedRegionMask.Clone(),
        rawVisibleMask: RawVisibleMask?.Clone());

    public void Dispose()
    {
        NuisanceMask.Dispose();
        StructureMask.Dispose();
        Edges.Dispose();
        ReferenceDistanceMap?.Dispose();
        ClippedReferenceDistanceMap?.Dispose();
        NormalizedGray.Dispose();
        foreach (var level in EdgePyramid)
            level.Dispose();
        Descriptors.Dispose();
        RepeatedRegionMask.Dispose();
        RawVisibleMask?.Dispose();
    }
}
