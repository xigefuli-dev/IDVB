using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// One supported corner on a binary local map structure contour.
/// RayA points to the preceding OpenCV contour vertex and RayB to the next
/// vertex; their order and the signed determinant retain contour winding.
/// Ray vectors are unit length, with lengths and angles reported separately.
/// </summary>
public readonly record struct MapLocalCornerGeometry(
    Point2d Point,
    Point2d RayA,
    Point2d RayB,
    double RayALength,
    double RayBLength,
    double RayAAngleDegrees,
    double RayBAngleDegrees,
    double DirectionDeterminant,
    int ContourIndex,
    int VertexIndex);

/// <summary>
/// Extracts locally supported polygon corners from binary structural edges.
/// This class only describes geometry; it does not rank map identities or
/// decide whether two corners constitute a match.
/// </summary>
public static class MapLocalCornerGeometryExtractor
{
    private const double ApproximationEpsilonPixels = 2d;
    private const double MinimumRayLengthPixels = 8d;
    private const double MinimumAbsoluteDirectionDeterminant = 0.45d;
    private const int SupportStartDistancePixels = 2;
    private const int MaximumSupportDistancePixels = 12;

    // One pixel covers the 3x3 dilation radius and two more cover the
    // polygon approximation tolerance. A point inside this band could be a
    // turn introduced solely by clipping a crop at its edge.
    private const int CropBoundaryGuardPixels = 3;

    /// <summary>
    /// Extracts corners from one binary edge image and uses that same image as
    /// the support source. For a live frame, pass its ObservedEdges matrix.
    /// </summary>
    public static IReadOnlyList<MapLocalCornerGeometry> ExtractCorners(
        Mat binaryStructuralEdges) =>
        ExtractCorners(binaryStructuralEdges, binaryStructuralEdges);

    /// <summary>
    /// Extracts contours from <paramref name="binaryStructuralEdges"/> and
    /// verifies both rays against a 5x5 dilation of
    /// <paramref name="edgeSupport"/>. The matrices must share the same
    /// local-pixel coordinate space and dimensions.
    /// </summary>
    public static IReadOnlyList<MapLocalCornerGeometry> ExtractCorners(
        Mat binaryStructuralEdges,
        Mat edgeSupport)
    {
        ArgumentNullException.ThrowIfNull(binaryStructuralEdges);
        ArgumentNullException.ThrowIfNull(edgeSupport);
        ValidateBinaryInput(binaryStructuralEdges, nameof(binaryStructuralEdges));
        ValidateBinaryInput(edgeSupport, nameof(edgeSupport));

        if (binaryStructuralEdges.Width != edgeSupport.Width
            || binaryStructuralEdges.Height != edgeSupport.Height)
        {
            throw new ArgumentException(
                "Structural edges and support must use the same image dimensions.",
                nameof(edgeSupport));
        }

        var width = binaryStructuralEdges.Width;
        var height = binaryStructuralEdges.Height;
        if (width <= CropBoundaryGuardPixels * 2
            || height <= CropBoundaryGuardPixels * 2)
        {
            return [];
        }

        using var binary = new Mat();
        Cv2.Threshold(binaryStructuralEdges, binary, 128, 255, ThresholdTypes.Binary);
        if (Cv2.CountNonZero(binary) == 0)
            return [];

        using var supportBinary = new Mat();
        Cv2.Threshold(edgeSupport, supportBinary, 128, 255, ThresholdTypes.Binary);
        using var supportKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(5, 5));
        using var dilatedSupport = new Mat();
        Cv2.Dilate(supportBinary, dilatedSupport, supportKernel);

        using var contourKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(3, 3));
        using var contourBand = new Mat();
        Cv2.Dilate(binary, contourBand, contourKernel);

        // FindContours may modify its source on some OpenCV versions.
        using var contourInput = contourBand.Clone();
        Cv2.FindContours(
            contourInput,
            out Point[][] contours,
            out _,
            RetrievalModes.List,
            ContourApproximationModes.ApproxSimple);

        var corners = new List<MapLocalCornerGeometry>();
        for (var contourIndex = 0; contourIndex < contours.Length; contourIndex++)
        {
            var contour = contours[contourIndex];
            if (contour.Length < 3)
                continue;

            var polygon = Cv2.ApproxPolyDP(
                contour,
                ApproximationEpsilonPixels,
                closed: true);
            if (polygon.Length < 3)
                continue;

            // Visit vertices in the unmodified order returned by OpenCV.
            // Do not normalize winding or sort candidates after extraction.
            for (var vertexIndex = 0; vertexIndex < polygon.Length; vertexIndex++)
            {
                var previous = polygon[(vertexIndex + polygon.Length - 1) % polygon.Length];
                var point = polygon[vertexIndex];
                var next = polygon[(vertexIndex + 1) % polygon.Length];

                if (!IsSafelyInsideCrop(previous, width, height)
                    || !IsSafelyInsideCrop(point, width, height)
                    || !IsSafelyInsideCrop(next, width, height))
                {
                    continue;
                }

                var rayAVector = new Point2d(previous.X - point.X, previous.Y - point.Y);
                var rayBVector = new Point2d(next.X - point.X, next.Y - point.Y);
                var rayALength = Length(rayAVector);
                var rayBLength = Length(rayBVector);
                if (rayALength < MinimumRayLengthPixels
                    || rayBLength < MinimumRayLengthPixels)
                {
                    continue;
                }

                var rayA = new Point2d(
                    rayAVector.X / rayALength,
                    rayAVector.Y / rayALength);
                var rayB = new Point2d(
                    rayBVector.X / rayBLength,
                    rayBVector.Y / rayBLength);
                var determinant = (rayA.X * rayB.Y) - (rayA.Y * rayB.X);
                if (Math.Abs(determinant) <= MinimumAbsoluteDirectionDeterminant)
                    continue;

                var corner = new Point2d(point.X, point.Y);
                if (!HasRaySupport(corner, rayA, rayALength, dilatedSupport)
                    || !HasRaySupport(corner, rayB, rayBLength, dilatedSupport))
                {
                    continue;
                }

                corners.Add(new MapLocalCornerGeometry(
                    corner,
                    rayA,
                    rayB,
                    rayALength,
                    rayBLength,
                    ToDegrees(rayA),
                    ToDegrees(rayB),
                    determinant,
                    contourIndex,
                    vertexIndex));
            }
        }

        return corners;
    }

    private static bool HasRaySupport(
        Point2d origin,
        Point2d unitRay,
        double rayLength,
        Mat dilatedSupport)
    {
        var lastDistance = Math.Min(MaximumSupportDistancePixels, rayLength);
        if (lastDistance < SupportStartDistancePixels)
            return false;

        // Every integer sample in the near-corner ray must be backed by the
        // actual support image. This rejects a contour bend unsupported by the
        // observed wall, without inventing geometry across unknown/fog pixels.
        for (var distance = SupportStartDistancePixels;
             distance <= lastDistance;
             distance++)
        {
            var x = (int)Math.Round(
                origin.X + (unitRay.X * distance),
                MidpointRounding.AwayFromZero);
            var y = (int)Math.Round(
                origin.Y + (unitRay.Y * distance),
                MidpointRounding.AwayFromZero);
            if ((uint)x >= (uint)dilatedSupport.Width
                || (uint)y >= (uint)dilatedSupport.Height
                || dilatedSupport.At<byte>(y, x) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSafelyInsideCrop(Point point, int width, int height) =>
        point.X >= CropBoundaryGuardPixels
        && point.Y >= CropBoundaryGuardPixels
        && point.X < width - CropBoundaryGuardPixels
        && point.Y < height - CropBoundaryGuardPixels;

    private static double Length(Point2d vector) =>
        Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));

    private static double ToDegrees(Point2d unitRay) =>
        Math.Atan2(unitRay.Y, unitRay.X) * (180d / Math.PI);

    private static void ValidateBinaryInput(Mat matrix, string parameterName)
    {
        if (matrix.Type() != MatType.CV_8UC1)
        {
            throw new ArgumentException(
                "Local corner extraction requires a single-channel 8-bit edge image.",
                parameterName);
        }
    }
}
