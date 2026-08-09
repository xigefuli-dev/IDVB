using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public enum MapDisplayCalibrationSource
{
    Exact = 0,
    Migrated = 1,
    Derived = 2
}

public sealed class MapDisplayCalibrationProfile
{
    public int SchemaVersion { get; set; } = 1;
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public NormalizedRectangle? MapViewportRegion { get; set; }
    public NormalizedRectangle? FloorDisplayRegion { get; set; }
    public uint LastObservedDpi { get; set; }
    public MapDisplayCalibrationSource Source { get; set; } =
        MapDisplayCalibrationSource.Exact;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsValid =>
        ClientWidth > 0
        && ClientHeight > 0
        && (MapViewportRegion?.IsValid is true
            || FloorDisplayRegion?.IsValid is true);

    public MapDisplayCalibrationProfile Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        ClientWidth = ClientWidth,
        ClientHeight = ClientHeight,
        MapViewportRegion = MapViewportRegion?.Clone(),
        FloorDisplayRegion = FloorDisplayRegion?.Clone(),
        LastObservedDpi = LastObservedDpi,
        Source = Source,
        UpdatedAt = UpdatedAt
    };
}

public sealed partial class MapRuntimeSettings
{
    public List<MapDisplayCalibrationProfile> DisplayCalibrationProfiles
    {
        get;
        set;
    } = [];

    public MapDisplayCalibrationProfile? GetExactDisplayCalibration(
        int clientWidth,
        int clientHeight) =>
        DisplayCalibrationProfiles
            .Where(profile =>
                profile.IsValid
                && profile.ClientWidth == clientWidth
                && profile.ClientHeight == clientHeight)
            .OrderByDescending(profile => profile.UpdatedAt)
            .FirstOrDefault();

    public NormalizedRectangle? ResolveMapViewportRegion(
        int clientWidth,
        int clientHeight) =>
        GetExactDisplayCalibration(clientWidth, clientHeight)
            ?.MapViewportRegion?.Clone()
        ?? GetClosestDisplayCalibration(clientWidth, clientHeight)
            ?.MapViewportRegion?.Clone()
        ?? MapViewportRegion?.Clone();

    public NormalizedRectangle? ResolveFloorDisplayRegion(
        int clientWidth,
        int clientHeight) =>
        GetExactDisplayCalibration(clientWidth, clientHeight)
            ?.FloorDisplayRegion?.Clone()
        ?? GetClosestDisplayCalibration(clientWidth, clientHeight)
            ?.FloorDisplayRegion?.Clone()
        ?? FloorDisplayRegion?.Clone();

    public void UpsertMapViewportCalibration(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight,
        uint observedDpi)
    {
        var profile = GetOrCreateDisplayCalibration(clientWidth, clientHeight);
        profile.MapViewportRegion = region.Clone();
        profile.LastObservedDpi = observedDpi;
        profile.Source = MapDisplayCalibrationSource.Exact;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        MapViewportRegion = region.Clone();
        CalibrationClientWidth = clientWidth;
        CalibrationClientHeight = clientHeight;
        CalibrationVersion = CurrentCalibrationVersion;
    }

    public void UpsertFloorDisplayCalibration(
        NormalizedRectangle region,
        int clientWidth,
        int clientHeight,
        uint observedDpi)
    {
        var profile = GetOrCreateDisplayCalibration(clientWidth, clientHeight);
        profile.FloorDisplayRegion = region.Clone();
        profile.LastObservedDpi = observedDpi;
        profile.Source = MapDisplayCalibrationSource.Exact;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        FloorDisplayRegion = region.Clone();
        FloorCalibrationClientWidth = clientWidth;
        FloorCalibrationClientHeight = clientHeight;
        FloorCalibrationVersion = CurrentCalibrationVersion;
    }

    private MapDisplayCalibrationProfile GetOrCreateDisplayCalibration(
        int clientWidth,
        int clientHeight)
    {
        var profile = GetExactDisplayCalibration(clientWidth, clientHeight);
        if (profile is not null)
            return profile;
        profile = new MapDisplayCalibrationProfile
        {
            ClientWidth = clientWidth,
            ClientHeight = clientHeight,
            Source = MapDisplayCalibrationSource.Derived
        };
        DisplayCalibrationProfiles.Add(profile);
        return profile;
    }

    private MapDisplayCalibrationProfile? GetClosestDisplayCalibration(
        int clientWidth,
        int clientHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0)
            return null;
        var targetAspect = (double)clientWidth / clientHeight;
        return DisplayCalibrationProfiles
            .Where(profile => profile.IsValid)
            .OrderBy(profile =>
                Math.Abs(((double)profile.ClientWidth / profile.ClientHeight)
                    - targetAspect))
            .ThenBy(profile =>
                Math.Abs(profile.ClientWidth - clientWidth)
                + Math.Abs(profile.ClientHeight - clientHeight))
            .FirstOrDefault();
    }

    private void NormalizeDisplayCalibrationProfiles()
    {
        DisplayCalibrationProfiles ??= [];
        if (MapViewportRegion?.IsValid is true
            && CalibrationClientWidth > 0
            && CalibrationClientHeight > 0)
        {
            var migrated = GetOrCreateDisplayCalibration(
                CalibrationClientWidth,
                CalibrationClientHeight);
            migrated.MapViewportRegion ??= MapViewportRegion.Clone();
            if (migrated.Source != MapDisplayCalibrationSource.Exact)
                migrated.Source = MapDisplayCalibrationSource.Migrated;
        }
        if (FloorDisplayRegion?.IsValid is true
            && FloorCalibrationClientWidth > 0
            && FloorCalibrationClientHeight > 0)
        {
            var migrated = GetOrCreateDisplayCalibration(
                FloorCalibrationClientWidth,
                FloorCalibrationClientHeight);
            migrated.FloorDisplayRegion ??= FloorDisplayRegion.Clone();
            if (migrated.Source != MapDisplayCalibrationSource.Exact)
                migrated.Source = MapDisplayCalibrationSource.Migrated;
        }

        DisplayCalibrationProfiles = DisplayCalibrationProfiles
            .Where(profile => profile?.IsValid is true)
            .GroupBy(profile => (profile.ClientWidth, profile.ClientHeight))
            .Select(group => group
                .OrderByDescending(profile => profile.UpdatedAt)
                .First()
                .Clone())
            .ToList();
    }
}
