using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class MapReferenceBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1d;
    public double Height { get; set; } = 1d;

    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0d
        && Height > 0d;

    public MapReferenceBounds Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height
    };

    public static MapReferenceBounds FullImage(int width, int height) => new()
    {
        Width = Math.Max(1, width),
        Height = Math.Max(1, height)
    };

    public bool Contains(MapReferencePoint point, double tolerance = 0d) =>
        point.IsFinite
        && point.X >= X - tolerance
        && point.Y >= Y - tolerance
        && point.X <= Right + tolerance
        && point.Y <= Bottom + tolerance;

    public MapReferencePoint Clamp(MapReferencePoint point) => new(
        Math.Clamp(point.X, X, Right),
        Math.Clamp(point.Y, Y, Bottom));

    public MapViewportOrigin ClampViewportOrigin(
        MapViewportOrigin origin,
        double viewportWidth,
        double viewportHeight)
    {
        if (!IsValid
            || !origin.IsFinite
            || !double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0d
            || viewportHeight <= 0d)
        {
            return new MapViewportOrigin(X, Y);
        }

        // A native map canvas can be larger than the projected reference map.
        // In that case the valid origin interval is reversed: the reference
        // may sit anywhere between the canvas's left/top and right/bottom
        // edges while remaining fully visible.
        var minimumX = Math.Min(X, Right - viewportWidth);
        var maximumX = Math.Max(X, Right - viewportWidth);
        var minimumY = Math.Min(Y, Bottom - viewportHeight);
        var maximumY = Math.Max(Y, Bottom - viewportHeight);
        return new MapViewportOrigin(
            Math.Clamp(origin.X, minimumX, maximumX),
            Math.Clamp(origin.Y, minimumY, maximumY));
    }
}
