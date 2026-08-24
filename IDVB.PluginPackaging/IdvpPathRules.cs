using System.Text;
using System.Text.RegularExpressions;

namespace IdentityVisionBridge.PluginPackaging;

internal static partial class IdvpPathRules
{
    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionRegex();

    public static bool IsIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && IdentifierRegex().IsMatch(value);

    public static bool IsSemanticVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && SemanticVersionRegex().IsMatch(value);

    public static string ValidateArchivePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.IndexOf('\0') >= 0)
        {
            throw new IdvpPackageException("The package contains an invalid or empty path.");
        }

        if (!string.Equals(value, value.Normalize(NormalizationForm.FormC), StringComparison.Ordinal))
        {
            throw new IdvpPackageException($"Package path is not Unicode-normalized: {value}");
        }

        var normalized = value.Replace('\\', '/');
        if (!string.Equals(value, normalized, StringComparison.Ordinal) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            throw new IdvpPackageException($"Package path must be a relative forward-slash path: {value}");
        }

        var segments = normalized.Split('/');
        if (segments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment) || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
        {
            throw new IdvpPackageException($"Package path contains an unsafe segment: {value}");
        }

        return normalized;
    }

    public static string ResolveExtractionPath(string rootDirectory, string archivePath)
    {
        var normalized = ValidateArchivePath(archivePath);
        var root = Path.GetFullPath(rootDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new IdvpPackageException($"Package path escapes the extraction root: {archivePath}");
        }

        return fullPath;
    }
}
