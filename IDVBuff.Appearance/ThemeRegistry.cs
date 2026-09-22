using System.Collections.Frozen;

namespace IDVBuff.Appearance;

/// <summary>Immutable preset data. Adding a palette does not add page-level branches.</summary>
public sealed class ThemeDefinition
{
    public string Id { get; }
    public string DisplayName { get; }
    public FrozenDictionary<ThemeToken, RgbColor> Light { get; }
    public FrozenDictionary<ThemeToken, RgbColor> Dark { get; }
    public RgbColor LightAccent { get; }
    public RgbColor DarkAccent { get; }
    public FrozenSet<ThemeMaterial> Materials { get; }

    public ThemeDefinition(string id, string displayName,
        IReadOnlyDictionary<ThemeToken, RgbColor> light, IReadOnlyDictionary<ThemeToken, RgbColor> dark,
        RgbColor lightAccent, RgbColor darkAccent, IEnumerable<ThemeMaterial> materials)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("配色方案需要稳定标识和显示名称。");
        Id = id;
        DisplayName = displayName;
        Light = light.ToFrozenDictionary();
        Dark = dark.ToFrozenDictionary();
        LightAccent = lightAccent;
        DarkAccent = darkAccent;
        Materials = materials.ToFrozenSet();
        if (!Materials.Contains(ThemeMaterial.Solid) || Materials.Any(material => !Enum.IsDefined(material)))
            throw new ArgumentException("配色方案必须提供纯色回退，且只能声明已支持的材质。");
        foreach (var palette in new[] { Light, Dark })
        foreach (var token in Enum.GetValues<ThemeToken>().Where(token => token is < ThemeToken.Accent or > ThemeToken.FocusInner))
            if (!palette.ContainsKey(token)) throw new ArgumentException($"配色方案 {id} 缺少资源 {token}。");
    }
}

public sealed class ThemeRegistry
{
    private readonly FrozenDictionary<string, ThemeDefinition> _definitions;
    public static ThemeRegistry BuiltIn { get; } = new([NeutralTheme.Definition]);
    public IEnumerable<ThemeDefinition> Definitions => _definitions.Values;

    public ThemeRegistry(IEnumerable<ThemeDefinition> definitions)
    {
        var items = definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        if (items.Count == 0) throw new ArgumentException("至少需要一个完整配色方案。");
        _definitions = items.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public ThemeDefinition Get(string id) => _definitions.TryGetValue(id, out var definition)
        ? definition : throw new ArgumentException($"未登记的配色方案：{id}");
}
