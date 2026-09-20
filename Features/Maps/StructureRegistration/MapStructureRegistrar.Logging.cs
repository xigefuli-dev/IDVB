namespace IDVBuff.Features.Maps;

public sealed partial class MapStructureRegistrar
{
    private static void LogEccRefinement(
        MapStructureCandidate refined,
        MapStructureRefiner.EccRefinementDiagnostics? diagnostics,
        double elapsedMilliseconds)
    {
        MapLogCollector.Instance.Append(
            MapLogCategory.StructureRegistration,
            MapLogLevel.Info,
            $"ECC精修完成 · 收敛={refined.EccConverged}",
            elapsedMs: elapsedMilliseconds,
            details: new()
            {
                ["eccConverged"] = refined.EccConverged,
                ["eccCorrelation"] = refined.EccCorrelation,
                ["eccExecuted"] = diagnostics?.Executed ?? false,
                ["eccDownsampled"] = diagnostics?.Downsampled ?? false,
                ["eccOriginalWidth"] = diagnostics?.OriginalWidth ?? 0,
                ["eccOriginalHeight"] = diagnostics?.OriginalHeight ?? 0,
                ["eccExecutionWidth"] = diagnostics?.ExecutionWidth ?? 0,
                ["eccExecutionHeight"] = diagnostics?.ExecutionHeight ?? 0,
                ["eccExecutionScale"] = diagnostics?.ExecutionScale ?? 1d,
                ["eccSkipReason"] = diagnostics?.SkipReason
                    ?? "validation-skipped"
            });
    }
}
