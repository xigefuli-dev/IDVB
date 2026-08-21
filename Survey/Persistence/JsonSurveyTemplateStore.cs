using System.Text.Json;
using System.Text.Json.Serialization;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Persistence.Sqlite;

/// <summary>
/// Stores reusable survey color templates in one human-readable JSON file.
/// Writes are atomic so a failed save cannot leave a half-written config file.
/// </summary>
public sealed class JsonSurveyTemplateStore : ISurveyTemplateStore
{
    public const string DefaultFileName = "color-templates.json";
    public const int CurrentSchemaVersion = 1;

    private const int MaximumTemplateCount = 256;
    private const int MaximumTemplateNameLength = 128;
    private const int MaximumEntriesPerTemplate = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public JsonSurveyTemplateStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async Task<IReadOnlyList<SurveyColorTemplate>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return [];

            try
            {
                await using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                var document = await JsonSerializer.DeserializeAsync<TemplateDocument>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (document is null || document.SchemaVersion > CurrentSchemaVersion)
                    return [];

                return Sanitize(document.Templates
                    .Select(template => template.ToModel())
                    .OfType<SurveyColorTemplate>());
            }
            catch (JsonException)
            {
                // A damaged preference file must not prevent the editor from opening.
                return [];
            }
            catch (IOException)
            {
                // A temporarily unavailable preference file is treated like an empty one.
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IReadOnlyList<SurveyColorTemplate> templates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templates);

        var sanitized = Sanitize(templates);
        if (sanitized.Count != templates.Count)
            throw new ArgumentException("模板配置包含无效模板。", nameof(templates));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var document = new TemplateDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                Templates = sanitized
                    .Select(TemplateDocument.FromModel)
                    .ToList()
            };

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Preserve the original persistence exception.
            }

            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<SurveyColorTemplate> Sanitize(
        IEnumerable<SurveyColorTemplate>? templates)
    {
        var result = new List<SurveyColorTemplate>();
        if (templates is null)
            return result;

        foreach (var template in templates)
        {
            if (result.Count >= MaximumTemplateCount
                || template is null
                || template.Id == Guid.Empty
                || string.IsNullOrWhiteSpace(template.Name)
                || template.Name.Trim().Length > MaximumTemplateNameLength
                || template.Name.Any(char.IsControl)
                || template.Entries is null
                || template.Entries.Count == 0
                || template.Entries.Count > MaximumEntriesPerTemplate)
            {
                continue;
            }

            var entries = template.Entries
                .Where(entry => entry is not null)
                .Distinct()
                .ToArray();
            if (entries.Length == 0)
                continue;

            result.Add(new SurveyColorTemplate(
                template.Id,
                template.Name.Trim(),
                entries));
        }

        return result;
    }

    private sealed class TemplateDocument
    {
        public int SchemaVersion { get; set; }

        public List<TemplateModel> Templates { get; set; } = [];

        public static TemplateModel FromModel(SurveyColorTemplate template) => new()
        {
            Id = template.Id,
            Name = template.Name,
            Entries = template.Entries
                .Select(entry => new EntryModel
                {
                    R = entry.R,
                    G = entry.G,
                    B = entry.B,
                    Type = entry.Type
                })
                .ToList()
        };
    }

    private sealed class TemplateModel
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public List<EntryModel>? Entries { get; set; }

        public SurveyColorTemplate? ToModel()
        {
            if (Name is null || Entries is null)
                return null;
            return new SurveyColorTemplate(
                Id,
                Name,
                Entries.Select(entry => new SurveyColorTemplateEntry(
                    entry.R,
                    entry.G,
                    entry.B,
                    entry.Type)).ToArray());
        }
    }

    private sealed class EntryModel
    {
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
        public SurveyTemplateColorType Type { get; set; }
    }
}
