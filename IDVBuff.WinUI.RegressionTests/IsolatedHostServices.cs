// Only non-UI host boundaries are replaced. The controls, native templates,
// theme scopes/resources/dialog helper, and window styles are production code.
// No test reads or writes the installed application's preferences or map data.
namespace IDVBuff
{
    internal static class AppDataPaths
    {
        internal static string RootDirectory => throw new InvalidOperationException("Native regression tests must not access persisted window settings.");
    }
}

namespace IDVBuff.Lifecycle
{
    internal static class MainProgramPreferences
    {
        public static void SaveAppearance(IDVBuff.Appearance.AppearancePreferences preferences) { }
    }
}

namespace IDVBuff.Diagnostics
{
    internal static class OutputLog
    {
        public static void Write(string level, string area, string message, Exception? error = null) =>
            System.Diagnostics.Debug.WriteLine($"{level} {area}: {message} {error}");
    }
}

namespace IDVBuff.Features.Maps
{
    public readonly record struct MapScreenRect(double X, double Y, double Width, double Height)
    {
        public bool IsValid => Width > 0 && Height > 0;
    }

    internal static class MapRuntimeSettingsRules
    {
        public static string? ResolveMapClass(IReadOnlyList<string> classes, string? preferred) =>
            classes.FirstOrDefault(name => string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase)) ?? classes.FirstOrDefault();
    }

    public sealed record MapClassDiagnostic(string MapClass, bool IsHealthy, IReadOnlyList<string> Problems)
    {
        public string Summary => string.Join(Environment.NewLine, Problems);
    }

    // Synthetic diagnostics enter the same SnapshotChanged callback consumed by
    // the production panel; repository/image integrity is tested separately.
    internal sealed class MapClassDiagnosticCoordinator
    {
        public static MapClassDiagnosticCoordinator Instance { get; } = new();
        public IReadOnlyDictionary<string, MapClassDiagnostic> Snapshot { get; private set; } =
            new Dictionary<string, MapClassDiagnostic>(StringComparer.OrdinalIgnoreCase);
        public event Action? SnapshotChanged;

        public void Publish(params MapClassDiagnostic[] diagnostics)
        {
            Snapshot = diagnostics.ToDictionary(item => item.MapClass, StringComparer.OrdinalIgnoreCase);
            SnapshotChanged?.Invoke();
        }
    }
}
