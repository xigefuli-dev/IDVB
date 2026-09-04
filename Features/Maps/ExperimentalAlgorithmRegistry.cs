namespace IDVBuff.Features.Maps;

public sealed record ExperimentalAlgorithmOption(
    string Id,
    string DisplayName,
    Func<MapRuntimeSettings, bool> IsEnabled,
    Func<SessionOrchestrator, bool, Task> SetEnabledAsync);

public static class ExperimentalAlgorithmRegistry
{
    public static IReadOnlyList<ExperimentalAlgorithmOption> All { get; } =
    [
        new(
            "prebuilt-structure-line",
            "使用预制线图",
            settings => settings.StructureRegistrationTuning.UsePrebuiltStructureLine,
            (runtime, enabled) => runtime.SetUsePrebuiltStructureLineAsync(enabled))
    ];
}
