namespace IDVBuff.Modules;

internal static class ModuleNavigationRules
{
    /// <summary>
    /// Safe-mode views deliberately have no runtime service graph. Standalone
    /// pages also remain available while normal-mode runtime services start.
    /// </summary>
    internal static Task GetRequiredServicesReadyTask(
        string moduleId,
        bool isSafeMode,
        bool isServicesReady,
        Task servicesReadyTask) =>
        isSafeMode || isServicesReady || IsStandaloneModule(moduleId)
            ? Task.CompletedTask
            : servicesReadyTask;

    // New runtime modules require an explicit safe-mode implementation before
    // their normal factory can be used without the runtime service graph.
    internal static bool IsSafeModeRestrictedModule(string moduleId) =>
        !IsStandaloneModule(moduleId)
        && !string.Equals(moduleId, "map-list", StringComparison.OrdinalIgnoreCase);

    private static bool IsStandaloneModule(string moduleId) =>
        moduleId.ToLowerInvariant() is
            "home" or "help" or "main-settings" or "account"
            or "settings" or "tags-templates" or "fundamentals";
}
