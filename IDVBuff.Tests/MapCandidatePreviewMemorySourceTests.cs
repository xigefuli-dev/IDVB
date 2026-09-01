namespace IDVBuff.Tests;

public sealed class MapCandidatePreviewMemorySourceTests
{
    [Fact]
    public void PreparedCandidatePreviewsHaveABoundedDecodeWidth()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "Features", "Maps", "MapManualCandidateWindow.Preview.cs"));

        Assert.Contains("CandidatePreviewDecodeWidth = 640", source);
        Assert.Contains("DecodePixelWidth = CandidatePreviewDecodeWidth", source);
        Assert.Contains("using var preview = ResizeForPreview(positioned)", source);
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
