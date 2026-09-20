using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Defines the spatial reference frame of a VPSG 3.0 live observation.
/// </summary>
public enum Vpsg3CoordinateSpace
{
    /// <summary>
    /// Local pixel coordinate space within the cropped query viewport.
    /// (0, 0) is the top-left corner of the observation.
    /// Physical screen coordinates = (ViewportBounds.X + X, ViewportBounds.Y + Y).
    /// </summary>
    LocalViewport = 0,
}

public readonly record struct Vpsg3SparseSamplingDiagnostics(
    int TotalEdgePoints,
    int RequestedSparsePoints,
    int ActualSparsePoints,
    double SamplingStep,
    string SparsePointHash,
    int TopLeftCount,
    int TopRightCount,
    int BottomLeftCount,
    int BottomRightCount);

/// <summary>
/// Immutable, self-contained live geometry observation extracted from a game frame.
/// Manages the native memory lifecycle of ObservedEdges and ValidMask.
/// </summary>
public sealed class Vpsg3LiveObservation : IDisposable
{
    private Mat? _observedEdges;
    private Mat? _validMask;
    private int _disposed;

    /// <summary>Single-channel 8-bit binary image of observed structural edges (255=edge, 0=background).</summary>
    public Mat ObservedEdges =>
        _observedEdges ?? throw new ObjectDisposedException(nameof(Vpsg3LiveObservation));

    /// <summary>
    /// Single-channel 8-bit validity mask (255=known/explored/valid, 0=unknown/fog/HUD).
    /// Unknown fog regions represent absence of observation rather than confirmed open space.
    /// </summary>
    public Mat ValidMask =>
        _validMask ?? throw new ObjectDisposedException(nameof(Vpsg3LiveObservation));

    public int Width { get; }
    public int Height { get; }
    public int EdgePixelCount { get; }
    public int ValidStructurePixelCount { get; }

    /// <summary>Coordinate frame of ObservedEdges and ValidMask (strictly LocalViewport).</summary>
    public Vpsg3CoordinateSpace CoordinateSpace { get; } = Vpsg3CoordinateSpace.LocalViewport;

    /// <summary>Screen viewport bounding rectangle where this observation was captured from.</summary>
    public MapScreenRect ViewportBounds { get; }

    private Point[]? _sparseEdgePoints;
    private readonly int _maxSparsePoints;
    private readonly object _sparseLock = new();

    /// <summary>
    /// Pre-sampled sparse edge points in LocalViewport coordinates for O(1) bit-test verification.
    /// Evaluated lazily on first access with fast native pointer scanning, incurring zero cost if unused.
    /// </summary>
    public IReadOnlyList<Point> SparseEdgePoints
    {
        get
        {
            if (IsDisposed) throw new ObjectDisposedException(nameof(Vpsg3LiveObservation));
            if (_sparseEdgePoints is not null)
                return _sparseEdgePoints;

            lock (_sparseLock)
            {
                if (_sparseEdgePoints is null)
                {
                    _sparseEdgePoints = SampleSparseEdgePointsFast(_observedEdges!, EdgePixelCount, _maxSparsePoints);
                }
                return _sparseEdgePoints;
            }
        }
    }

    /// <summary>Compact identity of the exact sparse vote set, for diagnostics only.</summary>
    public Vpsg3SparseSamplingDiagnostics GetSparseSamplingDiagnostics()
    {
        var points = SparseEdgePoints;
        var quadrants = new int[4];
        ulong hash = 14695981039346656037UL;
        foreach (var point in points)
        {
            var quadrant = (point.Y >= Height / 2 ? 2 : 0) + (point.X >= Width / 2 ? 1 : 0);
            quadrants[quadrant]++;
            unchecked
            {
                hash = (hash ^ (uint)point.X) * 1099511628211UL;
                hash = (hash ^ (uint)point.Y) * 1099511628211UL;
            }
        }

        return new(
            EdgePixelCount,
            _maxSparsePoints,
            points.Count,
            points.Count == 0 ? 0d : (double)EdgePixelCount / points.Count,
            hash.ToString("X16"),
            quadrants[0], quadrants[1], quadrants[2], quadrants[3]);
    }

    /// <summary>Total extraction time in milliseconds.</summary>
    public double ExtractionMilliseconds { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Vpsg3LiveObservation(
        Mat observedEdges,
        Mat validMask,
        int width,
        int height,
        int edgePixelCount,
        int validStructurePixelCount,
        MapScreenRect viewportBounds,
        int maxSparsePoints = 150,
        Point[]? sparseEdgePoints = null,
        double extractionMilliseconds = 0)
    {
        _observedEdges = observedEdges ?? throw new ArgumentNullException(nameof(observedEdges));
        _validMask = validMask ?? throw new ArgumentNullException(nameof(validMask));
        Width = width;
        Height = height;
        EdgePixelCount = edgePixelCount;
        ValidStructurePixelCount = validStructurePixelCount;
        ViewportBounds = viewportBounds;
        _maxSparsePoints = maxSparsePoints;
        _sparseEdgePoints = sparseEdgePoints;
        ExtractionMilliseconds = extractionMilliseconds;
    }

    private static Point[] SampleSparseEdgePointsFast(Mat edges, int edgeCount, int maxPts)
    {
        if (maxPts <= 0 || edgeCount <= 0 || edges.Empty())
            return [];

        using var nonZero = new Mat();
        Cv2.FindNonZero(edges, nonZero);

        var total = nonZero.Rows * nonZero.Cols;
        if (total == 0)
            return [];

        var sampleCount = Math.Min(maxPts, total);
        var result = new Point[sampleCount];
        var step = (double)total / sampleCount;

        for (var i = 0; i < sampleCount; i++)
        {
            var idx = (int)(i * step);
            result[i] = nonZero.At<Point>(idx, 0);
        }

        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _observedEdges?.Dispose();
            _observedEdges = null;
            _validMask?.Dispose();
            _validMask = null;
        }
    }
}
