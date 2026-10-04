using System.Drawing;

namespace IDVBuff.Tests;

public sealed class HomeGameShelfAssetTests
{
    [Fact]
    public void IdentityVTileUsesPackagedHighResolutionSquareArtwork()
    {
        var root = FindRepositoryRoot();
        var imagePath = Path.Combine(root, "Assets", "Games", "identity-v.png");

        Assert.True(File.Exists(imagePath), $"Missing game artwork: {imagePath}");
        using var image = Image.FromFile(imagePath);
        Assert.Equal(image.Width, image.Height);
        Assert.True(image.Width >= 256,
            $"Game artwork must be at least 256px for high-DPI display; actual width was {image.Width}px.");

        var project = File.ReadAllText(Path.Combine(root, "IDVBuff.csproj"));
        Assert.Contains(@"<Content Include=""Assets\Games\*.png"" CopyToOutputDirectory=""PreserveNewest"" />", project);
    }

    [Fact]
    public void AddGamePlaceholderIsDrawnAsVectorGeometryInsteadOfEmoji()
    {
        var source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Views", "HomePage.GameShelf.cs"));

        Assert.Contains("CreateAddGamePlaceholder", source);
        Assert.DoesNotContain("➕", source);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "IDVBuff.csproj")))
            current = current.Parent;

        return current?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
