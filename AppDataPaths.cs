namespace IDVBuff;

/// <summary>Separates manual test builds from the user's production data.</summary>
public static class AppDataPaths
{
#if IDVBUFF_TEST_BUILD
    public const bool IsTestBuild = true;
    public const string ProductDirectoryName = "IDVB-Test";
    private const string LegacyProductDirectoryName = "IDVBuff-Test";
    public const string DisplayName = "Identity Vision Bridge（测试版）";
#else
    public const bool IsTestBuild = false;
    public const string ProductDirectoryName = "IDVB";
    private const string LegacyProductDirectoryName = "IDVBuff";
    public const string DisplayName = "Identity Vision Bridge";
#endif

    private static readonly Lazy<string> ResolvedRoot = new(ResolveRootDirectory);
    public static string RootDirectory => ResolvedRoot.Value;

    private static string ResolveRootDirectory()
    {
#if IDVB_UNIT_TEST
        // Unit tests must never migrate or write the user's installed data.
        return Path.Combine(Path.GetTempPath(), $"IDVB-UnitTests-{Environment.ProcessId}");
#else
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var targetDirectory = Path.Combine(localAppData, ProductDirectoryName);
        var legacyDirectory = Path.Combine(localAppData, LegacyProductDirectoryName);

        try
        {
            if (!Directory.Exists(legacyDirectory))
                return targetDirectory;

            if (!Directory.Exists(targetDirectory))
            {
                Directory.Move(legacyDirectory, targetDirectory);
                return targetDirectory;
            }

            MoveMissingLegacyEntries(legacyDirectory, targetDirectory);
        }
        catch
        {
            // Keeping the old directory intact is safer than risking user data.
        }

        return targetDirectory;
#endif
    }

    internal static readonly object CacheIoGate = new();
    private static readonly Dictionary<string, int> ActiveCachePaths = new(StringComparer.OrdinalIgnoreCase);

    internal static IDisposable ProtectCachePath(string path)
    {
        path = Path.GetFullPath(path);
        lock (CacheIoGate)
            ActiveCachePaths[path] = ActiveCachePaths.GetValueOrDefault(path) + 1;
        return new CachePathLease(path);
    }

    internal static string[] GetActiveCachePaths()
    {
        lock (CacheIoGate) return ActiveCachePaths.Keys.ToArray();
    }

    private sealed class CachePathLease(string path) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            lock (CacheIoGate)
            {
                if (_disposed) return;
                _disposed = true;
                if (--ActiveCachePaths[path] == 0) ActiveCachePaths.Remove(path);
            }
        }
    }

    private static void MoveMissingLegacyEntries(
        string legacyDirectory,
        string targetDirectory)
    {
        foreach (var sourcePath in Directory.EnumerateFileSystemEntries(legacyDirectory))
        {
            var destinationPath = Path.Combine(
                targetDirectory,
                Path.GetFileName(sourcePath));
            if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
                continue;

            if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, destinationPath);
            else
                File.Move(sourcePath, destinationPath);
        }
    }
}
