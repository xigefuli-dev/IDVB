using System.Xml.Linq;

namespace IDVBuff.Tests;

/// <summary>
/// Guards against MSBuild scheduling the same project twice with different global property bags.
/// That produces two C# compiler nodes writing the same obj output and surfaces as CS2012 even
/// after the desktop application itself has exited.
/// </summary>
public sealed class BuildGraphLockRegressionTests
{
    [Fact]
    public void SolutionBuildsBothTheMainApplicationAndWinUiEditor()
    {
        var projects = ReadSolutionProjects();

        Assert.Contains("IDVBuff.csproj", projects);
        Assert.Contains("Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj", projects);
    }

    [Fact]
    public void MainApplicationEditorReferenceDoesNotInjectGlobalProperties()
    {
        var reference = ReadProjectReferences("IDVBuff.csproj")
            .Single(reference => reference.Include == "Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj");

        Assert.True(string.IsNullOrWhiteSpace(reference.AdditionalProperties));
    }

    [Fact]
    public void MainApplicationEditorReferenceDoesNotInjectWindowsAppSdkSelfContained()
    {
        var reference = ReadProjectReferences("IDVBuff.csproj")
            .Single(reference => reference.Include == "Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj");

        Assert.DoesNotContain("WindowsAppSDKSelfContained", reference.AdditionalProperties ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectSolutionProjectsAreNotRebuiltThroughPropertyChangingReferences()
    {
        var solutionProjects = ReadSolutionProjects().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var violatingReferences = ReadProjectReferences("IDVBuff.csproj")
            .Where(reference => solutionProjects.Contains(reference.Include)
                && !string.IsNullOrWhiteSpace(reference.AdditionalProperties))
            .ToArray();

        Assert.Empty(violatingReferences);
    }

    [Fact]
    public void WinUiEditorHasOneBuildIdentityInTheSolutionGraph()
    {
        var editorPath = "Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj";
        var identities = new[]
        {
            new ProjectBuildIdentity(editorPath, "obj\\Debug\\net10.0-windows10.0.19041.0\\", ""),
            new ProjectBuildIdentity(editorPath, "obj\\Debug\\net10.0-windows10.0.19041.0\\",
                ReadProjectReferences("IDVBuff.csproj").Single(reference => reference.Include == editorPath)
                    .AdditionalProperties ?? "")
        };

        Assert.Empty(BuildGraphLockRisk.FindCollisions(identities));
    }

    [Fact]
    public void OriginalEditorPropertyInjectionIsRecognizedAsAnObjWriteCollision()
    {
        var collisions = BuildGraphLockRisk.FindCollisions(
        [
            new("Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj", "obj\\Debug\\net10.0-windows\\", ""),
            new("Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj", "obj\\Debug\\net10.0-windows\\",
                "WindowsAppSDKSelfContained=false")
        ]);

        var collision = Assert.Single(collisions);
        Assert.Equal("Survey\\Editor\\IDVBuff.Survey.Editor.WinUI.csproj", collision.ProjectPath);
        Assert.Contains("WindowsAppSDKSelfContained=false", collision.ConflictingProperties);
    }

    [Fact]
    public void IdenticalProjectBuildIdentitiesDoNotCollide()
    {
        var collisions = BuildGraphLockRisk.FindCollisions(
        [
            new("Editor.csproj", "obj\\Debug\\net10.0\\", ""),
            new("Editor.csproj", "obj\\Debug\\net10.0\\", "")
        ]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void DifferentIntermediateOutputsDoNotCollide()
    {
        var collisions = BuildGraphLockRisk.FindCollisions(
        [
            new("Editor.csproj", "obj\\Debug\\net10.0\\", "A=true"),
            new("Editor.csproj", "obj\\Release\\net10.0\\", "A=false")
        ]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void DifferentPropertyOrderingDoesNotCreateAFalseCollision()
    {
        var collisions = BuildGraphLockRisk.FindCollisions(
        [
            new("Editor.csproj", "obj\\Debug\\net10.0\\", "A=true;B=false"),
            new("Editor.csproj", "obj\\Debug\\net10.0\\", "B=false;A=true")
        ]);

        Assert.Empty(collisions);
    }

    [Fact]
    public void MultipleGlobalPropertyVariantsOfTheSameOutputAreAllReported()
    {
        var collisions = BuildGraphLockRisk.FindCollisions(
        [
            new("Editor.csproj", "obj\\Debug\\net10.0\\", ""),
            new("Editor.csproj", "obj\\Debug\\net10.0\\", "A=true"),
            new("Editor.csproj", "obj\\Debug\\net10.0\\", "A=false")
        ]);

        Assert.Equal(2, collisions.Count);
    }

    [Fact]
    public void ReleaseWorkflowShutsDownBuildServersAndRefusesToProceedWhenHostsRemain()
    {
        var workflow = Read("release", "Invoke-IDVBUpdateWorkflow.ps1");

        Assert.Contains("function Assert-BuildProcessesClosed", workflow);
        Assert.Contains("dotnet build-server shutdown", workflow);
        Assert.Contains("Cannot start the release build because .NET/MSBuild processes remain", workflow);
    }

    [Fact]
    public void BuildHostCleanupNeverForceKillsProcesses()
    {
        var workflow = Read("release", "Invoke-IDVBUpdateWorkflow.ps1");
        var release = Read("release", "Invoke-IDVBRelease.ps1");

        Assert.DoesNotContain("Stop-Process", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stop-Process", release, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("taskkill", release, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadSolutionProjects() => XDocument.Load(Path.Combine(Root(), "IDVBuff.slnx"))
        .Descendants("Project")
        .Select(element => element.Attribute("Path")?.Value)
        .OfType<string>()
        .ToArray();

    private static IReadOnlyList<ProjectReference> ReadProjectReferences(params string[] path) =>
        XDocument.Load(Path.Combine([Root(), .. path]))
            .Descendants("ProjectReference")
            .Select(element => new ProjectReference(
                element.Attribute("Include")?.Value ?? "",
                element.Attribute("AdditionalProperties")?.Value))
            .ToArray();

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path]));

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IDVBuff.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("IDVB repository root was not found.");
    }

    private sealed record ProjectReference(string Include, string? AdditionalProperties);
    private sealed record ProjectBuildIdentity(string ProjectPath, string IntermediateOutputPath, string GlobalProperties);

    private sealed record BuildCollision(string ProjectPath, string ConflictingProperties);

    private static class BuildGraphLockRisk
    {
        public static IReadOnlyList<BuildCollision> FindCollisions(IEnumerable<ProjectBuildIdentity> identities) => identities
            .GroupBy(identity => (identity.ProjectPath, identity.IntermediateOutputPath), StringTupleComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                group.Key.ProjectPath,
                Properties = group.Select(identity => NormalizeProperties(identity.GlobalProperties))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .Where(group => group.Properties.Length > 1)
            .SelectMany(group => group.Properties.Skip(1).Select(properties =>
                new BuildCollision(group.ProjectPath, properties)))
            .ToArray();

        private static string NormalizeProperties(string properties) => string.Join(";", properties
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string ProjectPath, string IntermediateOutputPath)>
    {
        public static StringTupleComparer OrdinalIgnoreCase { get; } = new();

        public bool Equals((string ProjectPath, string IntermediateOutputPath) x,
            (string ProjectPath, string IntermediateOutputPath) y) =>
            string.Equals(x.ProjectPath, y.ProjectPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.IntermediateOutputPath, y.IntermediateOutputPath, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ProjectPath, string IntermediateOutputPath) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProjectPath),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.IntermediateOutputPath));
    }
}
