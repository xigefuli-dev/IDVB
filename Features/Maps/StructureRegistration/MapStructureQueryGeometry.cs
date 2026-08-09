using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed class QueryGeometry : IDisposable
{
    public QueryGeometry(
        double scale,
        Mat structure,
        Mat edges,
        Rect bounds,
        Point[] edgePoints,
        Mat? visibleMask = null)
    {
        Scale = scale;
        Structure = structure;
        Edges = edges;
        Bounds = bounds;
        EdgePoints = edgePoints;
        VisibleMask = visibleMask;
    }

    public double Scale { get; }
    public Mat Structure { get; }
    public Mat Edges { get; }
    public Rect Bounds { get; }
    public Point[] EdgePoints { get; }
    public int EdgeCount => EdgePoints.Length;
    public Mat? VisibleMask { get; }

    public QueryGeometry CloneForDebug() => new(
        Scale,
        Structure.Clone(),
        Edges.Clone(),
        Bounds,
        EdgePoints,
        VisibleMask?.Clone());

    public void Dispose()
    {
        Structure.Dispose();
        Edges.Dispose();
        VisibleMask?.Dispose();
    }
}
