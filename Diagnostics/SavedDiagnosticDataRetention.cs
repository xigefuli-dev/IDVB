namespace IDVBuff.Diagnostics;

/// <summary>Expires locally saved logs, alignment research, and diagnostic captures.</summary>
internal static class SavedDiagnosticDataRetention
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly object Gate = new();
    private static Timer? _timer;

    internal static void Start()
    {
        lock (Gate)
        {
            _timer ??= new Timer(static _ =>
            {
                try { Cleanup(AppDataPaths.RootDirectory, DateTime.UtcNow); }
                catch { /* Retention must not interrupt the application. */ }
            }, null, TimeSpan.Zero, TimeSpan.FromHours(1));
        }
    }

    internal static void Cleanup(string rootDirectory, DateTime nowUtc,
        string? activeLogPath = null, string? activeDiagnosticDirectory = null,
        string? activeResearchDirectory = null)
    {
        var cutoff = nowUtc - Retention;
        var logs = Path.Combine(rootDirectory, "Logs");
        var activeLog = activeLogPath ?? OutputLog.CurrentLogPath;
        var activeScanLog = Features.Maps.MapLogCollector.Instance?.CurrentSessionPath;
        foreach (var pattern in new[]
        {
            "output-log-*.log", "startup-*.log", "updater-*.log", "scan-log-*.json*",
            "startup.log", "updater.log", "flush-errors.log"
        })
            DeleteOldFiles(logs, pattern, cutoff, activeLog, activeScanLog);

        DeleteOldDirectories(Path.Combine(rootDirectory, "AlignmentResearch", "sessions"),
            "*", cutoff,
            activeResearchDirectory ?? Features.Maps.MapAlignmentResearchCollector.ActiveSessionDirectory);
        DeleteOldDirectories(Path.Combine(rootDirectory, "诊断模式"),
            "对局 *", cutoff,
            activeDiagnosticDirectory ?? Features.Maps.MapDiagnosticModeCapture.ActiveMatchDirectory);
    }

    private static void DeleteOldFiles(string directory, string pattern, DateTime cutoff,
        string? activePath, string? activeScanLog)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateFiles(directory, pattern))
            {
                try
                {
                    if (SamePath(path, activePath) || SamePath(path, activeScanLog)
                        || File.GetLastWriteTimeUtc(path) >= cutoff)
                        continue;
                    File.Delete(path);
                }
                catch { /* Continue past locked or inaccessible files. */ }
            }
        }
        catch { /* An unavailable directory must not block the other categories. */ }
    }

    private static void DeleteOldDirectories(string directory, string pattern,
        DateTime cutoff, string? activePath)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            foreach (var path in Directory.EnumerateDirectories(directory, pattern))
            {
                try
                {
                    var info = new DirectoryInfo(path);
                    if (SamePath(path, activePath)
                        || (info.Attributes & FileAttributes.ReparsePoint) != 0
                        || info.CreationTimeUtc >= cutoff)
                        continue;
                    info.Delete(recursive: true);
                }
                catch { /* Continue past locked or inaccessible sessions. */ }
            }
        }
        catch { /* An unavailable directory must not block the other categories. */ }
    }

    private static bool SamePath(string path, string? other) =>
        other is not null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(other),
            StringComparison.OrdinalIgnoreCase);
}
