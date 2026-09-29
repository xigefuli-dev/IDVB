using IDVBuff.UpdateCore;

namespace IDVBuff.Features.Maps;

public enum IdvmExportScope
{
    CurrentClass,
    AllClasses
}

public sealed record IdvmImportResult(
    Guid PackageId,
    IReadOnlyList<string> CreatedClasses,
    IReadOnlyList<MapRecord> ImportedMaps,
    IReadOnlyList<MapVariantGroup>? ImportedVariantGroups = null);

public sealed class IdvmPlatformNotSupportedException : IOException
{
    public IdvmPlatformNotSupportedException(IReadOnlyList<string> supportedPlatforms)
        : base($"该 IDVM 地图包不支持 Windows。声明的平台：{string.Join("、", supportedPlatforms)}。")
    {
        SupportedPlatforms = supportedPlatforms;
    }

    public IReadOnlyList<string> SupportedPlatforms { get; }
}

public sealed partial class IdvmPackageService
{
    private static void ValidatePlatformCompatibility(ManifestDto manifest, byte[] manifestBytes)
    {
        // The original layout-identity 1.4 exporter predates platform declarations.
        // Only an absent field in that format retains its portable interpretation;
        // explicit empty, null or non-Windows declarations remain authoritative.
        using var document = System.Text.Json.JsonDocument.Parse(manifestBytes);
        if (manifest.Capabilities.LayoutIdentities
            && !document.RootElement.TryGetProperty("supportedPlatforms", out _))
            manifest.SupportedPlatforms = IdvmPlatformCompatibility.All.ToList();
        ValidateSupportedPlatforms(manifest.SupportedPlatforms);
        if (!IdvmPlatformCompatibility.SupportsWindows(manifest.SupportedPlatforms))
            throw new IdvmPlatformNotSupportedException(manifest.SupportedPlatforms!);
    }

    private static void ValidateSupportedPlatforms(IReadOnlyList<string>? platforms)
    {
        if (platforms is null || platforms.Count == 0
            || platforms.Count != platforms.Distinct(StringComparer.Ordinal).Count()
            || platforms.Any(platform => !IdvmPlatformCompatibility.IsKnown(platform)))
        {
            throw new InvalidDataException(
                "IDVM 1.4 supportedPlatforms 必须从 windows、android、ios、web 中声明至少一个平台，且不得重复。");
        }
    }
}
