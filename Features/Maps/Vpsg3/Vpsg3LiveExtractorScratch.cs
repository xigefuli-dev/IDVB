using OpenCvSharp;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Session-owned or worker-owned reusable scratch buffers for <see cref="Vpsg3FastLiveExtractor"/>.
/// Eliminates per-frame native Mat malloc/free churn, structuring element re-creation,
/// and intermediate managed collection allocations.
/// Thread-safety: Not thread-safe. A single scratch instance must be bound to a single worker/session thread.
/// </summary>
public sealed class Vpsg3LiveExtractorScratch : IDisposable
{
    private bool _isDisposed;

    // Precompiled reusable structuring elements
    public Mat K3 { get; } = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
    public Mat K5 { get; } = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
    public Mat K11 { get; } = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 11));

    // Reusable intermediate Mats
    public Mat Bgr { get; } = new();
    public Mat Hsv { get; } = new();
    public Mat Exclusion { get; } = new();
    public Mat GreenSeed { get; } = new();
    public Mat WhiteSeed { get; } = new();
    public Mat Room1 { get; } = new();
    public Mat Room2 { get; } = new();
    public Mat Room { get; } = new();
    public Mat Corridor { get; } = new();
    public Mat RoomEdges { get; } = new();
    public Mat CorridorEdges { get; } = new();
    public Mat CandidateEdges { get; } = new();
    public Mat Gray { get; } = new();
    public Mat CannyStrong { get; } = new();
    public Mat Support { get; } = new();
    public Mat NotSupport { get; } = new();
    public Mat UncertainFrontier { get; } = new();
    public Mat DilatedExclusion { get; } = new();
    public Mat Invalid { get; } = new();

    // Reusable contour batch buffers
    public List<Point[]> ApproxContourBatch { get; } = new(64);

    /// <summary>
    /// Resets/clears scratch intermediate Mats for a new extraction pass.
    /// </summary>
    public void PrepareForSize(Size size)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        // Ensure single-channel mask Mats are sized and cleared
        EnsureMatSized(Exclusion, size, MatType.CV_8UC1);
        Exclusion.SetTo(Scalar.Black);

        EnsureMatSized(CandidateEdges, size, MatType.CV_8UC1);
        CandidateEdges.SetTo(Scalar.Black);

        ApproxContourBatch.Clear();
    }

    public static void EnsureMatSized(Mat mat, Size size, MatType type)
    {
        if (mat.Size() != size || mat.Type() != type)
        {
            mat.Create(size, type);
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        K3.Dispose();
        K5.Dispose();
        K11.Dispose();

        Bgr.Dispose();
        Hsv.Dispose();
        Exclusion.Dispose();
        GreenSeed.Dispose();
        WhiteSeed.Dispose();
        Room1.Dispose();
        Room2.Dispose();
        Room.Dispose();
        Corridor.Dispose();
        RoomEdges.Dispose();
        CorridorEdges.Dispose();
        CandidateEdges.Dispose();
        Gray.Dispose();
        CannyStrong.Dispose();
        Support.Dispose();
        NotSupport.Dispose();
        UncertainFrontier.Dispose();
        DilatedExclusion.Dispose();
        Invalid.Dispose();
    }
}
