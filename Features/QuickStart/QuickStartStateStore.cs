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
    /// Only completion of the prompt suppresses it. Runtime settings can be
    /// persisted by safe-mode startup before the user ever sees quick-start.
    /// </summary>
    public bool ShouldShow
    {
        get
        {
            try
            {
                return !File.Exists(StatePath);
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
