using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed class MapEditorStateTests
{
    [Fact]
    public void OrdinaryCreationToolsRemainActiveButCropReturnsToSelect()
    {
        var state = new MapEditorToolState();
        foreach (var tool in new[] { MapEditorTool.Text, MapEditorTool.Line, MapEditorTool.Rectangle, MapEditorTool.Anchor })
        {
            state.Select(tool);
            state.CompleteCreation();
            Assert.Equal(tool, state.ActiveTool);
        }

        state.Select(MapEditorTool.Crop);
        state.CompleteCreation();
        Assert.Equal(MapEditorTool.Select, state.ActiveTool);
    }

    [Fact]
    public void GateToolIsAvailableOnEveryFloorAndPrimaryPairCommitsAtomically()
    {
        var state = new MapEditorToolState { FirstFloorKey = "ground", ActiveFloorKey = "upper" };
        Assert.True(state.Select(MapEditorTool.Gate));
        Assert.False(state.UsesPrimaryGatePair);

        state.ActiveFloorKey = "ground";
        Assert.True(state.Select(MapEditorTool.Gate));
        Assert.True(state.UsesPrimaryGatePair);
        var main = new NormalizedRectangle { X = .1, Y = .2, Width = .1, Height = .1 };
        var side = new NormalizedRectangle { X = .6, Y = .2, Width = .1, Height = .1 };
        state.StageMainGate(main);
        var transaction = state.CommitSideGate(side);

        Assert.NotNull(transaction);
        Assert.Equal(main.X, transaction.Value.Main.X);
        Assert.Equal(side.X, transaction.Value.Side.X);
        Assert.Equal(MapEditorTool.Select, state.ActiveTool);
    }

    [Fact]
    public void CancelingStagedGateDoesNotProduceACommit()
    {
        var state = new MapEditorToolState();
        state.Select(MapEditorTool.Gate);
        state.StageMainGate(new NormalizedRectangle { Width = .1, Height = .1 });

        Assert.True(state.CancelTransient());
        Assert.Null(state.PendingMainGate);
        Assert.Equal(MapEditorTool.Gate, state.ActiveTool);
    }

    [Fact]
    public void RecentColorsAreNormalizedDeduplicatedAndLimitedToFive()
    {
        var recent = new RecentAnnotationColors();
        foreach (var color in new[] { "#112233", "#223344", "#334455", "#445566", "#556677", "#667788", "#334455" })
            Assert.True(recent.Use(color));

        Assert.Equal(new[] { "#334455", "#667788", "#556677", "#445566", "#223344" }, recent.Colors);
        Assert.False(recent.Use("blue"));
    }

    [Fact]
    public async Task CorruptPreferencesFallBackSafely()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"idvb-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "preferences.json");
            await File.WriteAllTextAsync(path, "{ definitely not json");
            var repository = new MapEditorPreferencesRepository(path);
            Assert.Empty(await repository.LoadRecentColorsAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LineConstraintSnapsToAxesAndOptionalDiagonalsInCanvasSpace()
    {
        var start = new NormalizedPoint { X = .5, Y = .5 };
        var candidate = new NormalizedPoint { X = .8, Y = .6 };
        var axisOnly = MapEditorLineConstraints.Apply(start, candidate, 1000, 500, true, false);
        Assert.Equal(start.Y, axisOnly.Y, 6);

        var diagonalCandidate = new NormalizedPoint { X = .7, Y = .9 };
        var diagonal = MapEditorLineConstraints.Apply(start, diagonalCandidate, 1000, 500, true, true);
        Assert.Equal((diagonal.X - start.X) * 1000, (diagonal.Y - start.Y) * 500, 6);

        var unrestricted = MapEditorLineConstraints.Apply(start, candidate, 1000, 500, false, false);
        Assert.Equal(candidate.X, unrestricted.X, 6);
        Assert.Equal(candidate.Y, unrestricted.Y, 6);
    }

    [Fact]
    public void PreferencesNormalizeLegacyColorOnlyDataAndInvalidToolDefaults()
    {
        var preferences = new MapEditorPreferences
        {
            SchemaVersion = 1,
            RecentColors = ["#123456", "invalid"],
            TextDefaults = new MapEditorTextDefaults { FontSize = 18 },
            LineDefaults = new MapEditorLineDefaults
            {
                Mode = (MapEditorLineMode)99,
                AxisConstraintEnabled = false,
                AllowDiagonalConstraint = true
            }
        };

        preferences.Normalize();

        Assert.Equal(3, preferences.SchemaVersion);
        Assert.Equal(new[] { "#123456" }, preferences.RecentColors);
        Assert.Equal(MapEditorTextDefaults.DefaultFontSize, preferences.TextDefaults.FontSize);
        Assert.Equal(MapEditorLineMode.Free, preferences.LineDefaults.Mode);
        Assert.False(preferences.LineDefaults.AllowDiagonalConstraint);
        Assert.Equal(MapBackgroundLayerShape.Circle, preferences.ConcealDefaults.Shape);
        Assert.Equal(64, preferences.ConcealDefaults.BrushSizePixels);
    }
}
