namespace IDVBuff.Features.Maps;

/// <summary>
/// Match-scoped mini-map scales keyed by the target floor image. Resolving a
/// floor never consults another floor, so a switch can select its final scale
/// before the new image is presented.
/// </summary>
internal sealed class MiniMapFloorScaleState
{
    private readonly Dictionary<string, double> _scales =
        new(StringComparer.OrdinalIgnoreCase);

    public double Resolve(string floorImageKey, double baseScale) =>
        _scales.TryGetValue(floorImageKey, out var scale) ? scale : baseScale;

    public void Remember(string floorImageKey, double scale) =>
        _scales[floorImageKey] = scale;

    public void Clear() => _scales.Clear();
}
