namespace IDVBuff.Lifecycle;

internal static class VelopackInstallLayout
{
    public static bool IsValidLauncherPath(string launcherPath)
    {
        if (string.IsNullOrWhiteSpace(launcherPath))
            return false;

        string fullLauncherPath;
        try
        {
            fullLauncherPath = Path.GetFullPath(launcherPath);
        }
        catch
        {
            return false;
        }

        if (!string.Equals(
                Path.GetFileName(fullLauncherPath),
                "IDVB.exe",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullLauncherPath))
            return false;

        var installRoot = Path.GetDirectoryName(fullLauncherPath);
        if (string.IsNullOrWhiteSpace(installRoot))
            return false;

        var currentDirectory = Path.Combine(installRoot, "current");
        return File.Exists(Path.Combine(installRoot, "Update.exe"))
            && File.Exists(Path.Combine(currentDirectory, "IDVB.exe"))
            && File.Exists(Path.Combine(currentDirectory, "sq.version"));
    }
}
