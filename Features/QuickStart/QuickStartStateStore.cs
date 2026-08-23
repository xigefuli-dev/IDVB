namespace IDVBuff.Features.QuickStart;

/// <summary>
/// Persists whether the first-run quick-start prompt has been completed.
/// </summary>
public sealed class QuickStartStateStore
{
    public const string StateFileName = "quick-start.completed";

    private readonly string _rootDirectory;

    public QuickStartStateStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory ?? global::IDVBuff.AppDataPaths.RootDirectory;
    }

    public string StatePath => Path.Combine(_rootDirectory, StateFileName);

    /// <summary>
    /// New installations do not have either the completion marker or a persisted
    /// map settings file. Existing installations are not interrupted by this flow.
    /// </summary>
    public bool ShouldShow
    {
        get
        {
            try
            {
                return !File.Exists(StatePath)
                    && !File.Exists(Path.Combine(_rootDirectory, "MapRuntime", "settings.json"));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void MarkCompleted()
    {
        Directory.CreateDirectory(_rootDirectory);
        File.WriteAllText(StatePath, "completed");
    }
}
