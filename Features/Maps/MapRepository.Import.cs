using System.Text.Json;

namespace IDVBuff.Features.Maps;

public sealed partial class MapRepository
{
    /// <summary>
    /// Imports package classes as newly-created local classes. A durable journal
    /// lets a later process roll back a process-interrupted import.
    /// </summary>
    public async Task<MapImportBatchResult> ImportBatchAsync(
        IReadOnlyList<MapImportClassDraft> sourceClasses,
        CancellationToken cancellationToken = default)
    {
        if (sourceClasses.Count == 0 || sourceClasses.Any(item => item.Maps.Count == 0))
            throw new InvalidOperationException("IDVM 包不包含可导入的非空 Class。");

        Directory.CreateDirectory(_rootDirectory);
        var journalPath = Path.Combine(
            _rootDirectory,
            $".idvm-import-{Guid.NewGuid():N}.json");
        var journal = new IdvmImportJournal { ProcessId = Environment.ProcessId };
        WriteImportJournal(journalPath, journal);
        var imported = new List<MapRecord>();

        try
        {
            foreach (var sourceClass in sourceClasses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localClass = await CreateUniqueClassAsync(sourceClass.SourceName, uniqueName =>
                {
                    journal.CreatedClasses.Add(uniqueName);
                    WriteImportJournal(journalPath, journal);
                });

                foreach (var sourceDraft in sourceClass.Maps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sourceDraft.Id = Guid.NewGuid();
                    sourceDraft.CreateAsImportedCopy = true;
                    sourceDraft.Class = localClass;
                    journal.ImportedMapIds.Add(sourceDraft.Id.Value);
                    WriteImportJournal(journalPath, journal);
                    var saved = await SaveAsync(sourceDraft);
                    imported.Add(saved);
                }
            }

            journal.Completed = true;
            WriteImportJournal(journalPath, journal);
            File.Delete(journalPath);
            return new MapImportBatchResult(journal.CreatedClasses.ToArray(), imported.ToArray());
        }
        catch
        {
            var rolledBack = await RollBackImportAsync(journal);
            if (rolledBack && File.Exists(journalPath))
                File.Delete(journalPath);
            throw;
        }
    }

    internal static string BuildUniqueImportedClassName(
        string sourceName,
        IEnumerable<string> existingNames)
    {
        var occupied = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!occupied.Contains(sourceName))
            return sourceName;
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{sourceName} - 新添加{suffix}";
            if (!occupied.Contains(candidate))
                return candidate;
        }
        throw new InvalidOperationException("无法为导入的 Class 分配唯一名称。");
    }

    private async Task<bool> RollBackImportAsync(IdvmImportJournal journal)
    {
        var succeeded = true;
        foreach (var mapId in journal.ImportedMapIds.AsEnumerable().Reverse())
        {
            try
            {
                if ((await GetMapsAsync()).Any(map => map.Id == mapId))
                    await DeleteAsync(mapId);
            }
            catch { succeeded = false; }
        }
        foreach (var className in journal.CreatedClasses.AsEnumerable().Reverse())
        {
            try
            {
                if ((await GetCatalogSnapshotAsync()).Classes.Any(name => string.Equals(
                    name,
                    className,
                    StringComparison.OrdinalIgnoreCase)))
                    await DeleteClassAsync(className);
            }
            catch { succeeded = false; }
        }
        return succeeded;
    }

    private void RecoverInterruptedIdvmImports()
    {
        if (!Directory.Exists(_rootDirectory))
            return;
        foreach (var journalPath in Directory.EnumerateFiles(
            _rootDirectory,
            ".idvm-import-*.json",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<IdvmImportJournal>(
                    File.ReadAllBytes(journalPath),
                    SerializerOptions);
                if (journal is null)
                    continue;
                if (journal.Completed)
                {
                    File.Delete(journalPath);
                    continue;
                }

                var processStart = new DateTimeOffset(
                    System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());
                if (journal.ProcessId == Environment.ProcessId
                    && journal.StartedAtUtc >= processStart.AddSeconds(-5))
                {
                    continue;
                }

                if (File.Exists(CatalogPath))
                {
                    var catalog = JsonSerializer.Deserialize<MapCatalogDocument>(
                        File.ReadAllBytes(CatalogPath),
                        SerializerOptions) ?? new MapCatalogDocument();
                    var importedIds = journal.ImportedMapIds.ToHashSet();
                    catalog.Maps.RemoveAll(map => importedIds.Contains(map.Id));
                    catalog.Classes.RemoveAll(name => journal.CreatedClasses.Contains(
                        name,
                        StringComparer.OrdinalIgnoreCase));
                    if (catalog.Classes.Count == 0)
                        catalog.Classes.Add("S1");
                    var temporaryPath = $"{CatalogPath}.recovery-{Guid.NewGuid():N}";
                    File.WriteAllBytes(
                        temporaryPath,
                        JsonSerializer.SerializeToUtf8Bytes(catalog, SerializerOptions));
                    File.Move(temporaryPath, CatalogPath, overwrite: true);
                }

                foreach (var mapId in journal.ImportedMapIds)
                {
                    var directory = GetMapDirectory(mapId);
                    if (Directory.Exists(directory))
                        Directory.Delete(directory, recursive: true);
                }
                File.Delete(journalPath);
            }
            catch
            {
                // Keep an unreadable journal for diagnostics rather than risking
                // deletion of data whose transaction membership is unknown.
            }
        }
    }

    private static void WriteImportJournal(string path, IdvmImportJournal journal)
    {
        var temporaryPath = $"{path}.tmp";
        File.WriteAllBytes(
            temporaryPath,
            JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class IdvmImportJournal
    {
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        public bool Completed { get; set; }
        public List<string> CreatedClasses { get; set; } = [];
        public List<Guid> ImportedMapIds { get; set; } = [];
    }
}
