using System.Text.Json;

namespace IDVBuff.Features.Maps;

public enum MapEditorTool
{
    Select,
    Text,
    Line,
    Rectangle,
    Gate,
    Crop,
    Anchor,
    Pan
}

public enum MapEditorLineMode
{
    Free,
    Continuous
}

/// <summary>Defaults used when the text tool creates a new annotation.</summary>
public sealed class MapEditorTextDefaults
{
    public const double DefaultFontSize = 16d;

    /// <summary>Empty uses the platform default font family.</summary>
    public string FontFamily { get; set; } = string.Empty;
    public double FontSize { get; set; } = DefaultFontSize;
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsStrikethrough { get; set; }

    public MapEditorTextDefaults Clone() => new()
    {
        FontFamily = FontFamily,
        FontSize = FontSize,
        IsBold = IsBold,
        IsItalic = IsItalic,
        IsStrikethrough = IsStrikethrough
    };

    public void Normalize()
    {
        FontFamily = (FontFamily ?? string.Empty).Trim();
        FontSize = FontSize is 12d or 16d or 20d or 24d ? FontSize : DefaultFontSize;
    }
}

public sealed class MapEditorLineDefaults
{
    public MapEditorLineMode Mode { get; set; } = MapEditorLineMode.Free;
    public bool AxisConstraintEnabled { get; set; }
    public bool AllowDiagonalConstraint { get; set; }

    public MapEditorLineDefaults Clone() => new()
    {
        Mode = Mode,
        AxisConstraintEnabled = AxisConstraintEnabled,
        AllowDiagonalConstraint = AllowDiagonalConstraint
    };

    public void Normalize()
    {
        if (!Enum.IsDefined(Mode))
            Mode = MapEditorLineMode.Free;
        if (!AxisConstraintEnabled)
            AllowDiagonalConstraint = false;
    }
}

public static class MapEditorLineConstraints
{
    public static NormalizedPoint Apply(
        NormalizedPoint start,
        NormalizedPoint candidate,
        double canvasWidth,
        double canvasHeight,
        bool enabled,
        bool allowDiagonal)
    {
        if (!enabled)
            return candidate.Clone();
        var width = Math.Max(1d, canvasWidth);
        var height = Math.Max(1d, canvasHeight);
        var dx = (candidate.X - start.X) * width;
        var dy = (candidate.Y - start.Y) * height;
        if (Math.Abs(dx) < .000001d && Math.Abs(dy) < .000001d)
            return candidate.Clone();

        var angle = Math.Atan2(dy, dx);
        var directions = allowDiagonal
            ? new[] { 0d, Math.PI / 4, Math.PI / 2, 3 * Math.PI / 4, Math.PI, -3 * Math.PI / 4, -Math.PI / 2, -Math.PI / 4 }
            : new[] { 0d, Math.PI / 2, Math.PI, -Math.PI / 2 };
        var selected = directions.MinBy(direction => AngularDistance(angle, direction));
        var distance = dx * Math.Cos(selected) + dy * Math.Sin(selected);
        return new NormalizedPoint
        {
            X = Math.Clamp(start.X + (distance * Math.Cos(selected) / width), 0d, 1d),
            Y = Math.Clamp(start.Y + (distance * Math.Sin(selected) / height), 0d, 1d)
        };
    }

    private static double AngularDistance(double left, double right)
    {
        var difference = Math.Abs(left - right) % (2d * Math.PI);
        return difference > Math.PI ? 2d * Math.PI - difference : difference;
    }
}

public sealed class MapEditorPreferences
{
    public int SchemaVersion { get; set; } = 2;
    public List<string> RecentColors { get; set; } = [];
    public MapEditorTextDefaults TextDefaults { get; set; } = new();
    public MapEditorLineDefaults LineDefaults { get; set; } = new();

    public void Normalize()
    {
        SchemaVersion = 2;
        var colors = new RecentAnnotationColors();
        colors.Replace((RecentColors ?? []).AsEnumerable().Reverse());
        RecentColors = colors.Colors.ToList();
        TextDefaults ??= new MapEditorTextDefaults();
        TextDefaults.Normalize();
        LineDefaults ??= new MapEditorLineDefaults();
        LineDefaults.Normalize();
    }
}

/// <summary>Pure editor-tool state used by the WinUI surface and unit tests.</summary>
public sealed class MapEditorToolState
{
    public MapEditorTool ActiveTool { get; private set; } = MapEditorTool.Select;
    public string FirstFloorKey { get; set; } = "1f";
    public string ActiveFloorKey { get; set; } = "1f";
    public NormalizedRectangle? PendingMainGate { get; private set; }

    public bool Select(MapEditorTool tool)
    {
        if (tool == MapEditorTool.Gate
            && !string.Equals(ActiveFloorKey, FirstFloorKey, StringComparison.OrdinalIgnoreCase))
            return false;

        ActiveTool = tool;
        PendingMainGate = null;
        return true;
    }

    public void StageMainGate(NormalizedRectangle bounds) => PendingMainGate = bounds.Clone();

    public (NormalizedRectangle Main, NormalizedRectangle Side)? CommitSideGate(NormalizedRectangle side)
    {
        if (PendingMainGate?.IsValid is not true || !side.IsValid)
            return null;
        var transaction = (PendingMainGate.Clone(), side.Clone());
        PendingMainGate = null;
        ActiveTool = MapEditorTool.Select;
        return transaction;
    }

    public void CompleteCreation()
    {
        if (ActiveTool is not (MapEditorTool.Text or MapEditorTool.Line or MapEditorTool.Rectangle or MapEditorTool.Anchor))
            ActiveTool = MapEditorTool.Select;
    }

    /// <summary>Returns true when a transient door sequence was canceled.</summary>
    public bool CancelTransient()
    {
        if (PendingMainGate is null)
            return false;
        PendingMainGate = null;
        return true;
    }

    public void Reset()
    {
        ActiveTool = MapEditorTool.Select;
        PendingMainGate = null;
    }
}

public sealed class RecentAnnotationColors
{
    private readonly List<string> _colors = [];

    public IReadOnlyList<string> Colors => _colors;

    public void Replace(IEnumerable<string>? colors)
    {
        _colors.Clear();
        if (colors is null)
            return;
        foreach (var color in colors)
            Use(color);
    }

    public bool Use(string? color)
    {
        if (!MapAnnotationColor.TryNormalize(color, out var normalized))
            return false;
        _colors.RemoveAll(item => string.Equals(item, normalized, StringComparison.OrdinalIgnoreCase));
        _colors.Insert(0, normalized);
        if (_colors.Count > 5)
            _colors.RemoveRange(5, _colors.Count - 5);
        return true;
    }
}

public sealed class MapEditorPreferencesRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public MapEditorPreferencesRepository(string path) => _path = path;

    public async Task<MapEditorPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path))
                return new MapEditorPreferences();
            await using var stream = File.OpenRead(_path);
            var preferences = await JsonSerializer.DeserializeAsync<MapEditorPreferences>(stream, JsonOptions, cancellationToken)
                ?? new MapEditorPreferences();
            preferences.Normalize();
            return preferences;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new MapEditorPreferences();
        }
    }

    public async Task SaveAsync(MapEditorPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Normalize();
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, preferences, JsonOptions, cancellationToken);
        }
        File.Move(temporaryPath, _path, true);
    }

    public async Task<IReadOnlyList<string>> LoadRecentColorsAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).RecentColors;

    public async Task SaveRecentColorsAsync(IEnumerable<string> colors, CancellationToken cancellationToken = default)
    {
        var preferences = await LoadAsync(cancellationToken);
        var palette = new RecentAnnotationColors();
        palette.Replace(colors.AsEnumerable().Reverse());
        preferences.RecentColors = palette.Colors.ToList();
        await SaveAsync(preferences, cancellationToken);
    }
}
