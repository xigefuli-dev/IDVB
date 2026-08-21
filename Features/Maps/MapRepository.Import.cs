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
        var importedVariantGroups = new List<MapVariantGroup>();

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
                await SetImportedClassPropertiesAsync(localClass, sourceClass.Properties);
                var sourceToLocalMapIds = new Dictionary<Guid, Guid>();

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
                    if (sourceDraft.SourcePackageMapId is { } sourceMapId)
                        sourceToLocalMapIds.Add(sourceMapId, saved.Id);
                }
                var createdGroups = await CreateImportedVariantGroupsAsync(
                    localClass,
                    sourceClass.VariantGroups ?? [],
                    sourceToLocalMapIds);
                importedVariantGroups.AddRange(createdGroups);
                journal.ImportedVariantGroupIds.AddRange(createdGroups.Select(group => group.Id));
                WriteImportJournal(journalPath, journal);
            }

            journal.Completed = true;
            WriteImportJournal(journalPath, journal);
            File.Delete(journalPath);
            return new MapImportBatchResult(
                journal.CreatedClasses.ToArray(),
                imported.ToArray(),
                importedVariantGroups.Select(group => group.Clone()).ToArray());
        }
        catch
        {
            var rolledBack = await RollBackImportAsync(journal);
            if (rolledBack && File.Exists(journalPath))
                File.Delete(journalPath);
            throw;
        }
    }

    private async Task SetImportedClassPropertiesAsync(
        string className,
        MapClassProperties? properties)
    {
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            catalog.ClassProperties[className] = properties?.Clone() ?? new MapClassProperties();
            await WriteCatalogAsync(catalog);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<IReadOnlyList<MapVariantGroup>> CreateImportedVariantGroupsAsync(
        string className,
        IReadOnlyList<MapImportVariantGroupDraft> sourceGroups,
        IReadOnlyDictionary<Guid, Guid> sourceToLocalMapIds)
    {
        if (sourceGroups.Count == 0)
            return [];
        await Gate.WaitAsync();
        try
        {
            var catalog = await ReadCatalogAsync();
            var created = new List<MapVariantGroup>(sourceGroups.Count);
            foreach (var source in sourceGroups)
            {
                if (source.PaletteSlot is < 0 or >= MapVariantGroup.PaletteSize
                    || source.SourceMapIds.Count < 2
                    || source.SourceMapIds.Count != source.SourceMapIds.Distinct().Count()
                    || source.SourceMapIds.Any(mapId => !sourceToLocalMapIds.ContainsKey(mapId)))
                {
                    throw new InvalidDataException("IDVM 变体组无法映射到本次导入的地图。");
                }
                var group = new MapVariantGroup
                {
                    Id = Guid.NewGuid(),
                    Class = className,
                    PaletteSlot = source.PaletteSlot,
                    MapIds = source.SourceMapIds
                        .Select(mapId => sourceToLocalMapIds[mapId])
                        .ToList()
                };
                catalog.VariantGroups.Add(group);
                created.Add(group);
            }
            await WriteCatalogAsync(catalog);
            return created.Select(group => group.Clone()).ToArray();
        }
        finally
        {
            Gate.Release();
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
                    catalog.VariantGroups ??= [];
                    catalog.VariantGroups.RemoveAll(group =>
                        journal.CreatedClasses.Contains(group.Class, StringComparer.OrdinalIgnoreCase)
                        || group.MapIds.Any(importedIds.Contains));
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
        public List<Guid> ImportedVariantGroupIds { get; set; } = [];
    }
}
/*
 * 文件职责：MapRepository.Import。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
