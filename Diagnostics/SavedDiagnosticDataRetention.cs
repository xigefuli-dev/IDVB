namespace IDVBuff.Diagnostics;

/// <summary>Uses the manual cleanup's ownership and active-file rules.</summary>
internal static class SavedDiagnosticDataRetention
{
    internal static readonly TimeSpan Retention = LocalCacheCleanup.Retention;
    private static readonly object Gate = new();
    private static Timer? _timer;
    private static int _running;

    internal static void Start()
    {
        lock (Gate)
        {
            _timer ??= new Timer(static _ =>
            {
                if (Interlocked.Exchange(ref _running, 1) != 0) return;
                try { Cleanup(AppDataPaths.RootDirectory, DateTime.UtcNow); }
                catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Cache retention: {error.Message}"); }
                finally { Volatile.Write(ref _running, 0); }
            }, null, TimeSpan.Zero, TimeSpan.FromMinutes(10));
        }
    }

    internal static IEnumerable<string> ActivePaths() => new[]
    {
        OutputLog.CurrentLogPath,
        Features.Maps.MapLogCollector.Instance?.CurrentSessionPath,
        Features.Maps.MapAlignmentResearchCollector.ActiveSessionDirectory,
        Features.Maps.MapDiagnosticModeCapture.ActiveMatchDirectory
    }.Where(path => path is not null).Select(path => path!);

    internal static void Cleanup(string rootDirectory, DateTime nowUtc,
        string? activeLogPath = null, string? activeDiagnosticDirectory = null,
        string? activeResearchDirectory = null)
    {
        using var workflow = LocalCacheCleanup.TryBeginAutomatic();
        if (workflow is null) return;
        IEnumerable<string> Protected() => ActivePaths().Concat(
            new[] { activeLogPath, activeDiagnosticDirectory, activeResearchDirectory }
                .Where(path => path is not null).Select(path => path!));
        var plan = LocalCacheCleanup.Scan(rootDirectory, automatic: true, nowUtc, Protected);
        var result = LocalCacheCleanup.Execute(plan, Protected);
        foreach (var failure in result.Failures)
            System.Diagnostics.Debug.WriteLine($"Cache retention: {failure}");
    }
}
