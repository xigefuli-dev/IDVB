using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>Portable layout declaration; local map IDs and floor image paths remain independent.</summary>
public sealed class MapLayoutIdentity
{
    public Guid LayoutId { get; set; }
    public Dictionary<string, string> FloorKeys { get; set; } = new(StringComparer.Ordinal);

    public MapLayoutIdentity Clone() => new()
    {
        LayoutId = LayoutId,
        FloorKeys = new Dictionary<string, string>(FloorKeys, StringComparer.Ordinal)
    };

    internal void Validate(IEnumerable<string> floorKeys)
    {
        var declaredFloors = floorKeys.ToHashSet(StringComparer.Ordinal);
        if (LayoutId == Guid.Empty || FloorKeys is null || FloorKeys.Count != declaredFloors.Count
            || FloorKeys.Any(pair => !declaredFloors.Contains(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
            || FloorKeys.Values.Distinct(StringComparer.Ordinal).Count() != FloorKeys.Count)
            throw new InvalidDataException("布局身份必须包含非空 ID，并为每个楼层声明唯一的布局楼层键。");
    }
}

public sealed partial class MapRecord
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SourcePackageMapId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MapLayoutIdentity? LayoutIdentity { get; set; }
}

public sealed partial class MapDraft
{
    public MapLayoutIdentity? LayoutIdentity { get; set; }
}
