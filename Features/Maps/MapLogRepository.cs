using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Persistent storage for one map diagnostic session.
/// </summary>
public sealed class MapLogRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _logDirectory;

    public MapLogRepository(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "Logs");
    }

    public string LogDirectory => _logDirectory;

    public string CreateSessionPath()
    {
        Directory.CreateDirectory(_logDirectory);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(_logDirectory, $"scan-log-{timestamp}-{suffix}.json");
    }

    /// <summary>
    /// Merges a new batch into the session file and replaces it atomically.
    /// Keeping the on-disk format as a JSON array makes the files easy to inspect.
    /// </summary>
    public Task FlushAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken = default) =>
        MergeAndWriteAsync(sessionPath, entries, ".tmp", cancellationToken);

    public Task FinalizeAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        CancellationToken cancellationToken = default) =>
        MergeAndWriteAsync(sessionPath, entries, ".final.tmp", cancellationToken);

    public void DeleteSession(string sessionPath)
    {
        try
        {
            foreach (var path in new[]
            {
                sessionPath,
                sessionPath + ".tmp",
                sessionPath + ".final.tmp"
            })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    public void CleanupOldSessions(int keepCount = 20)
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return;

            var files = Directory.GetFiles(_logDirectory, "scan-log-*.json")
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ToArray();
            foreach (var file in files.Skip(Math.Max(0, keepCount)))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Skip files that cannot be deleted.
                }
            }

            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var tempFile in Directory.GetFiles(_logDirectory, "*.tmp"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(tempFile) < cutoff)
                        File.Delete(tempFile);
                }
                catch
                {
                    // Skip locked temporary files.
                }
            }
        }
        catch
        {
            // Cleanup is non-critical and must not stop log collection.
        }
    }

    private async Task MergeAndWriteAsync(
        string sessionPath,
        IReadOnlyList<MapLogEntry> entries,
        string temporarySuffix,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;

        Directory.CreateDirectory(_logDirectory);
        var merged = new List<MapLogEntry>();
        if (File.Exists(sessionPath))
        {
            await using var existingStream = File.OpenRead(sessionPath);
            var existing = await JsonSerializer.DeserializeAsync<List<MapLogEntry>>(
                existingStream,
                JsonOptions,
                cancellationToken);
            if (existing is not null)
                merged.AddRange(existing);
        }

        var knownSequences = merged.Select(entry => entry.Sequence).ToHashSet();
        foreach (var entry in entries)
        {
            if (knownSequences.Add(entry.Sequence))
                merged.Add(entry);
        }
        merged.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));

        var tempPath = sessionPath + temporarySuffix;
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    merged,
                    JsonOptions,
                    cancellationToken);
            }
            File.Move(tempPath, sessionPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best effort cleanup.
            }
            throw;
        }
    }
}
