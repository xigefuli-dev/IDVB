namespace IDVBuff.Survey.Persistence.Sqlite;

public sealed class SurveyStoragePaths
{
    public SurveyStoragePaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string ProjectDirectory(Guid projectId) =>
        Path.Combine(RootDirectory, projectId.ToString("N"));

    public string DatabasePath(Guid projectId) =>
        Path.Combine(ProjectDirectory(projectId), "project.db");

    public string AssetsDirectory(Guid projectId) =>
        Path.Combine(ProjectDirectory(projectId), "assets");

    public string TemporaryDirectory(Guid projectId) =>
        Path.Combine(ProjectDirectory(projectId), "temp");

    public string ResolveProjectRelativePath(Guid projectId, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var projectDirectory = Path.GetFullPath(ProjectDirectory(projectId));
        var candidate = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        var prefix = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? projectDirectory
            : projectDirectory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Survey asset path escapes the project directory.");
        return candidate;
    }
}
