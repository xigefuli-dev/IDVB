namespace IDVBuff.Tests;

public sealed class GameOverlayProgressBarSourceTests
{
    [Fact]
    public void ScanFailureUsesRedHighContrastTerminalState()
    {
        var root = FindRepositoryRoot();
        var progress = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "GameOverlayProgressBar.cs"));
        var operations = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "SessionOrchestrator.Operations.cs"));

        Assert.Contains("public void Fail(string message)", progress);
        Assert.Contains("Color.FromArgb(255, 179, 38, 30)", progress);
        Assert.Contains("failed ? \"失败\"", progress);
        Assert.Contains("using var white = new SolidBrush(Color.White)", progress);
        Assert.Contains("if (scanCompleted)", operations);
        Assert.Contains("_scanProgressOverlay.Fail(", operations);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
                return current.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
