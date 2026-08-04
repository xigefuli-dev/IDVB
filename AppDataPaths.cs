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

    public static string RootDirectory { get; } = ResolveRootDirectory();

    private static string ResolveRootDirectory()
    {
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
