namespace IDVBuff;

// SessionOrchestrator keeps this application-wide safety check on its GUI
// settings path. RealCLI is headless and never enables the runtime, but it
// still needs a deterministic application boundary when compiling the same
// orchestrator sources.
internal static class App
{
    public static bool IsSafeMode => false;
}
