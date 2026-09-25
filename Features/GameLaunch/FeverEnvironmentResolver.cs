using System.Diagnostics;
using Microsoft.Win32;

namespace IDVBuff.Features.GameLaunch;

/// <summary>
/// Resolves the installed FeverGames components (mpay.dll and skin.zip) for native authentication.
/// </summary>
internal static class FeverEnvironmentResolver
{
    private const string ProtocolCommandPath = @"fevergames\shell\open\command";
    private const string GameRegistryPath = @"Software\FeverGames\FeverGamesInstaller\game\73";

    public static bool TryResolve(out string mpayDllPath, out string skinZipPath, out string? failureReason)
    {
        var feverRoot = FindFeverRootDirectory();
        if (string.IsNullOrWhiteSpace(feverRoot) || !Directory.Exists(feverRoot))
        {
            mpayDllPath = string.Empty;
            skinZipPath = string.Empty;
            failureReason = "未找到网易发烧游戏安装目录。请确认网易发烧游戏已正确安装。";
            return false;
        }

        return TryResolveFromFeverRoot(
            feverRoot,
            Directory.GetDirectories,
            File.Exists,
            out mpayDllPath,
            out skinZipPath,
            out failureReason);
    }

    internal static bool TryResolveFromFeverRoot(
        string feverRoot,
        Func<string, IEnumerable<string>> getDirectories,
        Func<string, bool> fileExists,
        out string mpayDllPath,
        out string skinZipPath,
        out string? failureReason)
    {
        mpayDllPath = string.Empty;
        skinZipPath = string.Empty;
        failureReason = null;

        // Search for version directories (e.g. 1.18.44.2)
        IEnumerable<string> subDirs;
        try
        {
            subDirs = getDirectories(feverRoot);
        }
        catch (Exception ex)
        {
            failureReason = $"访问发烧游戏目录失败：{ex.Message}";
            return false;
        }

        var versionDirs = subDirs
            .Where(dir =>
            {
                var name = Path.GetFileName(dir);
                return Version.TryParse(name, out _) || name.All(c => char.IsDigit(c) || c == '.');
            })
            .OrderByDescending(dir => Version.TryParse(Path.GetFileName(dir), out var v) ? v : new Version(0, 0))
            .ThenByDescending(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in versionDirs)
        {
            var dll = Path.Combine(dir, "mpay.dll");
            var skin = Path.Combine(dir, "skin.zip");
            if (fileExists(dll) && fileExists(skin))
            {
                mpayDllPath = dll;
                skinZipPath = skin;
                return true;
            }
        }

        // Fallback: check root directly
        var rootDll = Path.Combine(feverRoot, "mpay.dll");
        var rootSkin = Path.Combine(feverRoot, "skin.zip");
        if (fileExists(rootDll) && fileExists(rootSkin))
        {
            mpayDllPath = rootDll;
            skinZipPath = rootSkin;
            return true;
        }

        failureReason = $"在发烧游戏目录 ({feverRoot}) 中未找到匹配的 mpay.dll 或 skin.zip 组件。";
        return false;
    }

    private static string? FindFeverRootDirectory()
    {
        // 1. Try protocol registration: HKCR\fevergames\shell\open\command
        try
        {
            using var protocolKey = Registry.ClassesRoot.OpenSubKey(ProtocolCommandPath, writable: false);
            var cmd = protocolKey?.GetValue(null) as string;
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                var exePath = ExtractExecutablePath(cmd);
                if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                {
                    var dir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                        return dir;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FeverEnvironmentResolver] Protocol registry probe failed: {ex.Message}");
        }

        // 2. Try common default installation paths
        string[] candidates =
        [
            @"D:\FeverGames",
            @"C:\FeverGames",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FeverGames"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FeverGames"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FeverGames")
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
                return path;
        }

        return null;
    }

    internal static string? ExtractExecutablePath(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? trimmed[..(executableEnd + 4)] : null;
    }

    public static string GetUnisdkDirectory()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApp, "FeverGames", "FeverGamesInstaller", "unisdk");
    }
}
