namespace IDVBuff.Tests;

public sealed class MapEditorBitmapLifetimeSourceTests
{
    [Fact]
    public void FloorSwitchReusesOneBitmapPerFloorAndExitReleasesIt()
    {
        var root = FindRepositoryRoot();
        var state = File.ReadAllText(Path.Combine(
            root, "Views", "MapListPage.ModernEditor.cs"));
        var switching = File.ReadAllText(Path.Combine(
            root, "Views", "MapListPage.ModernEditor.Part1.cs"));
        var catalog = File.ReadAllText(Path.Combine(
            root, "Views", "MapListPage.Catalog.cs"));

        Assert.Contains("_modernFloorBitmaps.TryGetValue(floorKey", switching);
        Assert.Contains("BitmapCreateOptions.IgnoreImageCache", switching);
        Assert.Contains("ModernEditorDecodePixelWidth = 2048", state);
        Assert.Contains("DecodePixelWidth = Math.Min(", switching);
        Assert.Contains("_modernScene.Width = sourceWidth", switching);
        Assert.Contains("entry.Bitmap.ImageOpened -= entry.OpenedHandler", state);
        Assert.Contains("entry.Bitmap.UriSource = null", state);
        Assert.Contains("_modernFloorBitmaps.Clear()", state);
        Assert.Contains("_modernCreationUndoStack.Clear()", state);
        Assert.Contains("_workflowHost.Content = null", state);
        Assert.Contains("ResetMarkerEditorSession();", catalog);
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
