using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed class MapTagGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public List<string> Tags { get; set; } = [];
    /// <summary>Classes that may display and edit this tag group.</summary>
    public List<string> AuthorizedClasses { get; set; } = [];
}

public static class MapTagAuthorizationRules
{
    public static bool IsAuthorized(
        MapTagGroup group,
        string? className,
        IEnumerable<MapRecord> maps)
    {
        if (string.IsNullOrWhiteSpace(className))
            return false;

        return group.AuthorizedClasses.Contains(className, StringComparer.OrdinalIgnoreCase)
            || maps.Any(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase)
                && map.Tags.ContainsKey(group.Id));
    }

    public static bool IsUsedByClass(
        MapTagGroup group,
        string className,
        IEnumerable<MapRecord> maps) =>
        maps.Any(map => string.Equals(map.Class, className, StringComparison.OrdinalIgnoreCase)
            && map.Tags.ContainsKey(group.Id));

    /// <summary>
    /// Repairs authorization without touching map tag values. A Class that
    /// already has a value in a group must always remain authorized.
    /// </summary>
    public static void PreserveUsedClassAuthorizations(
        IEnumerable<MapTagGroup> groups,
        IEnumerable<MapRecord> maps,
        IEnumerable<string>? classes = null)
    {
        var availableClasses = classes?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        foreach (var group in groups)
        {
            group.AuthorizedClasses ??= [];
            var authorized = group.AuthorizedClasses
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => availableClasses?.FirstOrDefault(candidate =>
                    string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) ?? name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var map in maps)
            {
                if (!map.Tags.ContainsKey(group.Id)
                    || string.IsNullOrWhiteSpace(map.Class)
                    || (availableClasses is not null
                        && !availableClasses.Any(candidate => string.Equals(
                            candidate, map.Class, StringComparison.OrdinalIgnoreCase))))
                    continue;
                var actualClass = availableClasses?.FirstOrDefault(candidate =>
                    string.Equals(candidate, map.Class, StringComparison.OrdinalIgnoreCase)) ?? map.Class;
                if (!authorized.Contains(actualClass, StringComparer.OrdinalIgnoreCase))
                    authorized.Add(actualClass);
            }

            group.AuthorizedClasses = authorized;
        }
    }
}

public sealed record MapFloorTemplate(string Key, string DisplayName);
public sealed record MapTemplate(string Id, string Name, IReadOnlyList<MapFloorTemplate> Floors);

public static class MapTemplates
{
    public static IReadOnlyList<MapTemplate> BuiltIn { get; } =
    [
        new("builtin-double", "常规双层",
        [
            new("1f", "1f"),
            new("2f", "2f")
        ]),
        new("builtin-basement", "包含地下室",
        [
            new("1f", "1f"),
            new("2f", "2f"),
            new("b1f", "地下室")
        ])
    ];
}

public sealed class MapTemplateStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public MapTemplateStore(string? path = null) => _path = path ?? Path.Combine(
        global::IDVBuff.AppDataPaths.RootDirectory, "map-templates.json");

    public async Task<IReadOnlyList<MapTemplate>> LoadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<TemplateDocument>(stream, JsonOptions);
            return Normalize(document?.Templates ?? []);
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(IEnumerable<MapTemplate> templates)
    {
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, new TemplateDocument
                {
                    Templates = Normalize(templates)
                }, JsonOptions);
            File.Move(temporary, _path, true);
        }
        finally { Gate.Release(); }
    }

    private static List<MapTemplate> Normalize(IEnumerable<MapTemplate> templates) => templates
        .Where(template => !string.IsNullOrWhiteSpace(template.Name) && template.Floors.Count > 0)
        .Where(template => !template.Id.StartsWith("builtin-", StringComparison.OrdinalIgnoreCase))
        .GroupBy(template => template.Id, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .Select(template => new MapTemplate(
            string.IsNullOrWhiteSpace(template.Id) ? $"custom-{Guid.NewGuid():N}" : template.Id,
            template.Name.Trim(),
            template.Floors.Where(floor => !string.IsNullOrWhiteSpace(floor.Key))
                .Select(floor => new MapFloorTemplate(floor.Key.Trim(), string.IsNullOrWhiteSpace(floor.DisplayName) ? floor.Key.Trim() : floor.DisplayName.Trim()))
                .GroupBy(floor => floor.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray()))
        .Where(template => template.Floors.Count > 0)
        .ToList();

    private sealed class TemplateDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<MapTemplate> Templates { get; set; } = [];
    }
}

public sealed class MapTagStore
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _path;

    public MapTagStore(string? path = null) => _path = path ?? Path.Combine(
        global::IDVBuff.AppDataPaths.RootDirectory, "map-tags.json");

    public async Task<IReadOnlyList<MapTagGroup>> LoadAsync(
        IEnumerable<MapRecord>? maps = null,
        IEnumerable<string>? classes = null)
    {
        await Gate.WaitAsync();
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream = File.OpenRead(_path);
            var document = await JsonSerializer.DeserializeAsync<TagDocument>(stream, JsonOptions);
            var groups = Normalize(document?.Groups ?? []);
            if (maps is not null)
                MapTagAuthorizationRules.PreserveUsedClassAuthorizations(groups, maps, classes);
            return groups;
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(
        IEnumerable<MapTagGroup> groups,
        IEnumerable<MapRecord>? maps = null,
        IEnumerable<string>? classes = null)
    {
        await Gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            var normalized = Normalize(groups);
            if (maps is not null)
                MapTagAuthorizationRules.PreserveUsedClassAuthorizations(normalized, maps, classes);
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, new TagDocument
                {
                    SchemaVersion = 1,
                    Groups = normalized
                }, JsonOptions);
            File.Move(temporary, _path, true);
        }
        finally { Gate.Release(); }
    }

    private static List<MapTagGroup> Normalize(IEnumerable<MapTagGroup> groups) => groups
        .Where(group => !string.IsNullOrWhiteSpace(group.Name))
        .GroupBy(group => group.Id)
        .Select(group => group.First())
        .Select(group => new MapTagGroup
        {
            Id = group.Id == Guid.Empty ? Guid.NewGuid() : group.Id,
            Name = group.Name.Trim(),
            IsEnabled = group.IsEnabled,
            Tags = (group.Tags ?? []).Select(tag => tag.Trim())
                .Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            AuthorizedClasses = (group.AuthorizedClasses ?? []).Select(name => name.Trim())
                .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        }).ToList();

    private sealed class TagDocument
    {
        public int SchemaVersion { get; set; } = 1;
        public List<MapTagGroup> Groups { get; set; } = [];
    }
}
