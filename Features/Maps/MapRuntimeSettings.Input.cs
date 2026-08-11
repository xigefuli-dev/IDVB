using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>One pass-through global input binding.</summary>
public sealed class MapInputBinding : IEquatable<MapInputBinding>
{
    public MapInputBindingKind Kind { get; set; }
    public uint VirtualKey { get; set; }
    public MapInputModifiers Modifiers { get; set; }
    public MapMouseButton MouseButton { get; set; }

    [JsonIgnore]
    public bool IsConfigured => Kind != MapInputBindingKind.None;

    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        MapInputBindingKind.Keyboard => FormatKeyboardDisplayName(),
        MapInputBindingKind.Mouse => MouseButton switch
        {
            MapMouseButton.Left => "鼠标左键",
            MapMouseButton.Right => "鼠标右键",
            MapMouseButton.Middle => "鼠标中键",
            MapMouseButton.XButton1 => "鼠标侧键 1",
            MapMouseButton.XButton2 => "鼠标侧键 2",
            _ => "鼠标按键"
        },
        _ => "未设置"
    };

    public MapInputBinding Clone() => new()
    {
        Kind = Kind,
        VirtualKey = VirtualKey,
        Modifiers = Modifiers,
        MouseButton = MouseButton
    };

    public bool Equals(MapInputBinding? other) => other is not null
        && Kind == other.Kind
        && VirtualKey == other.VirtualKey
        && Modifiers == other.Modifiers
        && MouseButton == other.MouseButton;

    public override bool Equals(object? obj) => Equals(obj as MapInputBinding);

    public override int GetHashCode() => HashCode.Combine(Kind, VirtualKey, Modifiers, MouseButton);

    private string FormatKeyboardDisplayName()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(MapInputModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(MapInputModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(MapInputModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(MapInputModifiers.Windows))
            parts.Add("Win");
        parts.Add(((Windows.System.VirtualKey)VirtualKey).ToString());
        return string.Join(" + ", parts);
    }
}
