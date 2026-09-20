namespace IdentityVisionBridge.PluginRuntime;

public sealed class PluginDirectories
{
    public PluginDirectories(string appDataRoot, bool developerMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        Root = Path.Combine(Path.GetFullPath(appDataRoot), "Plugins");
        if (developerMode)
        {
            Root = Path.Combine(Root, "Developer");
        }

        Packages = Path.Combine(Root, "Packages");
        Data = Path.Combine(Root, "Data");
        Staging = Path.Combine(Root, "Staging");
        CatalogPath = Path.Combine(Root, "plugin-catalog.json");
        TrustedPublishersPath = Path.Combine(Root, "trusted-publishers.json");
        LoadingMarkerPath = Path.Combine(Root, "loading-marker.json");
        SessionMarkerPath = Path.Combine(Root, "plugin-session.json");
        CrashStatePath = Path.Combine(Root, "plugin-crash-state.json");
        DeveloperMode = developerMode;
    }

    public string Root { get; private set; }

    public string Packages { get; }

    public string Data { get; }

    public string Staging { get; }

    public string CatalogPath { get; }

    public string TrustedPublishersPath { get; }

    public string LoadingMarkerPath { get; }

    public string SessionMarkerPath { get; }

    public string CrashStatePath { get; }

    public bool DeveloperMode { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Packages);
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Staging);
    }

    public string GetPackageDirectory(string pluginId, string version) =>
        Path.Combine(Packages, ValidateSegment(pluginId), ValidateSegment(version));

    public string GetDataDirectory(string publisherId, string pluginId) =>
        Path.Combine(Data, ValidateSegment(publisherId), ValidateSegment(pluginId));

    private static string ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar))
        {
            throw new ArgumentException("Invalid plugin path segment.", nameof(value));
        }

        return value;
    }
}
