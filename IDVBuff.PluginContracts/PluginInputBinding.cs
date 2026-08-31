namespace IDVBuff.PluginContracts;

/// <summary>插件设置页可录制的输入设备类型。</summary>
public enum PluginInputBindingKind
{
    None,
    Keyboard,
    Mouse
}

[Flags]
public enum PluginInputModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

public enum PluginMouseButton
{
    Left,
    Right,
    Middle,
    XButton1,
    XButton2
}

[Flags]
public enum PluginInputBindingKinds
{
    Keyboard = 1,
    Mouse = 2,
    All = Keyboard | Mouse
}

/// <summary>
/// 框架无关的插件输入绑定。StorageValue 是设置存储层使用的稳定字符串，
/// 这样插件设置仍遵守 SDK 只持久化 JSON 原语的约定。
/// </summary>
public sealed class PluginInputBinding : IEquatable<PluginInputBinding>
{
    public PluginInputBindingKind Kind { get; init; }

    public uint VirtualKey { get; init; }

    public PluginInputModifiers Modifiers { get; init; }

    /// <summary>Non-modifier keys that must remain held before VirtualKey fires.</summary>
    public IReadOnlyList<uint> CompanionVirtualKeys { get; init; } = [];

    public PluginMouseButton MouseButton { get; init; }

    public bool IsConfigured => Kind != PluginInputBindingKind.None;

    public string DisplayName => Kind switch
    {
        PluginInputBindingKind.Keyboard => FormatKeyboardDisplayName(),
        PluginInputBindingKind.Mouse => MouseButton switch
        {
            PluginMouseButton.Left => "鼠标左键",
            PluginMouseButton.Right => "鼠标右键",
            PluginMouseButton.Middle => "鼠标中键",
            PluginMouseButton.XButton1 => "鼠标侧键 1",
            PluginMouseButton.XButton2 => "鼠标侧键 2",
            _ => "鼠标按键"
        },
        _ => "未设置"
    };

    public string StorageValue => Kind switch
    {
        PluginInputBindingKind.Keyboard =>
            (CompanionVirtualKeys?.Count ?? 0) == 0
                ? $"keyboard:{VirtualKey:X}:{(int)Modifiers}"
                : $"keyboard:{VirtualKey:X}:{(int)Modifiers}:{string.Join(',', NormalizedCompanionVirtualKeys().Select(key => key.ToString("X")))}",
        PluginInputBindingKind.Mouse =>
            $"mouse:{(int)MouseButton}",
        _ => "none"
    };

    public PluginInputBinding Clone() => new()
    {
        Kind = Kind,
        VirtualKey = VirtualKey,
        Modifiers = Modifiers,
        CompanionVirtualKeys = [.. NormalizedCompanionVirtualKeys()],
        MouseButton = MouseButton
    };

    public bool Equals(PluginInputBinding? other) => other is not null
        && Kind == other.Kind
        && VirtualKey == other.VirtualKey
        && Modifiers == other.Modifiers
        && NormalizedCompanionVirtualKeys()
            .SequenceEqual(other.NormalizedCompanionVirtualKeys())
        && MouseButton == other.MouseButton;

    public override bool Equals(object? obj) => Equals(obj as PluginInputBinding);

    public override int GetHashCode() => HashCode.Combine(
        Kind, VirtualKey, Modifiers, MouseButton,
        string.Join(',', NormalizedCompanionVirtualKeys()));

    public static PluginInputBinding Keyboard(
        uint virtualKey,
        PluginInputModifiers modifiers = PluginInputModifiers.None,
        IEnumerable<uint>? companionVirtualKeys = null) => new()
    {
        Kind = PluginInputBindingKind.Keyboard,
        VirtualKey = virtualKey,
        Modifiers = modifiers,
        CompanionVirtualKeys = companionVirtualKeys?.ToArray() ?? []
    };

    public static PluginInputBinding Mouse(PluginMouseButton button) => new()
    {
        Kind = PluginInputBindingKind.Mouse,
        MouseButton = button
    };

    public static bool TryParse(string? value, out PluginInputBinding binding) =>
        TryParse(value, PluginInputBindingKinds.All, out binding);

    public static bool TryParse(
        string? value,
        PluginInputBindingKinds allowedKinds,
        out PluginInputBinding binding)
    {
        binding = new PluginInputBinding();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;

        var parts = value.Trim().Split(':');
        if (parts.Length is 3 or 4
            && parts[0].Equals("keyboard", StringComparison.OrdinalIgnoreCase)
            && allowedKinds.HasFlag(PluginInputBindingKinds.Keyboard)
            && uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                null, out var virtualKey)
            && int.TryParse(parts[2], out var modifiers)
            && virtualKey != 0
            && virtualKey <= ushort.MaxValue
            && modifiers >= 0
            && (modifiers
                & ~(int)(PluginInputModifiers.Control
                    | PluginInputModifiers.Alt
                    | PluginInputModifiers.Shift
                    | PluginInputModifiers.Windows)) == 0
            && TryParseCompanionVirtualKeys(parts, out var companionVirtualKeys))
        {
            binding = Keyboard(virtualKey, (PluginInputModifiers)modifiers,
                companionVirtualKeys);
            return true;
        }

        if (parts.Length == 2
            && parts[0].Equals("mouse", StringComparison.OrdinalIgnoreCase)
            && allowedKinds.HasFlag(PluginInputBindingKinds.Mouse)
            && int.TryParse(parts[1], out var button)
            && Enum.IsDefined((PluginMouseButton)button))
        {
            binding = Mouse((PluginMouseButton)button);
            return true;
        }

        return false;
    }

    private string FormatKeyboardDisplayName()
    {
        var parts = new List<string>(5 + (CompanionVirtualKeys?.Count ?? 0));
        if (Modifiers.HasFlag(PluginInputModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(PluginInputModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(PluginInputModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(PluginInputModifiers.Windows))
            parts.Add("Win");
        parts.AddRange(NormalizedCompanionVirtualKeys()
            .Select(InputKeyDisplayName.FormatVirtualKey));
        parts.Add(InputKeyDisplayName.FormatVirtualKey(VirtualKey));
        return string.Join(" + ", parts);
    }

    private IEnumerable<uint> NormalizedCompanionVirtualKeys() =>
        (CompanionVirtualKeys ?? [])
            .Where(key => key != 0 && key <= ushort.MaxValue && key != VirtualKey)
            .Distinct()
            .OrderBy(key => key);

    private static bool TryParseCompanionVirtualKeys(
        string[] parts,
        out uint[] companionVirtualKeys)
    {
        companionVirtualKeys = [];
        if (parts.Length == 3)
            return true;
        if (string.IsNullOrWhiteSpace(parts[3]))
            return false;
        var parsed = parts[3].Split(',')
            .Select(value => uint.TryParse(value,
                System.Globalization.NumberStyles.HexNumber, null, out var key)
                ? (uint?)key : null)
            .ToArray();
        if (parsed.Any(key => key is null or 0)
            || parsed.Any(key => key!.Value > ushort.MaxValue))
            return false;
        companionVirtualKeys = parsed.Select(key => key!.Value).ToArray();
        return true;
    }
}

/// <summary>插件绑定的按下/抬起事件。</summary>
public sealed class PluginInputEventArgs(
    string pluginId,
    string bindingKey,
    long timestamp,
    bool isDown) : EventArgs
{
    public string PluginId { get; } = pluginId;

    public string BindingKey { get; } = bindingKey;

    public long Timestamp { get; } = timestamp;

    public bool IsDown { get; } = isDown;
}

/// <summary>
/// PluginSDK 的插件级输入通道。宿主负责全局监听和生命周期，插件只维护自己的绑定键。
/// </summary>
public interface IPluginInputService
{
    event EventHandler<PluginInputEventArgs>? BindingInvoked;

    void SetBinding(string pluginId, string bindingKey, PluginInputBinding binding);

    void ClearBindings(string pluginId);

    bool IsBindingPressed(string pluginId, string bindingKey);
}
