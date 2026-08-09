namespace IDVBuff.Features.Maps;

public sealed class MapWindowSignature : IEquatable<MapWindowSignature>
{
    public long WindowHandle { get; init; }
    public int ClientX { get; init; }
    public int ClientY { get; init; }
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int ViewportX { get; init; }
    public int ViewportY { get; init; }
    public int ViewportWidth { get; init; }
    public int ViewportHeight { get; init; }
    public uint Dpi { get; init; } = 96;

    public bool Equals(MapWindowSignature? other) => other is not null
        && WindowHandle == other.WindowHandle
        && ClientX == other.ClientX
        && ClientY == other.ClientY
        && ClientWidth == other.ClientWidth
        && ClientHeight == other.ClientHeight
        && ViewportX == other.ViewportX
        && ViewportY == other.ViewportY
        && ViewportWidth == other.ViewportWidth
        && ViewportHeight == other.ViewportHeight
        && Dpi == other.Dpi;

    public override bool Equals(object? obj) => Equals(obj as MapWindowSignature);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WindowHandle);
        hash.Add(ClientX);
        hash.Add(ClientY);
        hash.Add(ClientWidth);
        hash.Add(ClientHeight);
        hash.Add(ViewportX);
        hash.Add(ViewportY);
        hash.Add(ViewportWidth);
        hash.Add(ViewportHeight);
        hash.Add(Dpi);
        return hash.ToHashCode();
    }
}
