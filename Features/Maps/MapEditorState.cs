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
    Pan,
    Conceal
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
    public int SchemaVersion { get; set; } = 3;
    public List<string> RecentColors { get; set; } = [];
    public MapEditorTextDefaults TextDefaults { get; set; } = new();
    public MapEditorLineDefaults LineDefaults { get; set; } = new();
    public MapEditorConcealDefaults ConcealDefaults { get; set; } = new();

    public void Normalize()
    {
        SchemaVersion = 3;
        var colors = new RecentAnnotationColors();
        colors.Replace((RecentColors ?? []).AsEnumerable().Reverse());
        RecentColors = colors.Colors.ToList();
        TextDefaults ??= new MapEditorTextDefaults();
        TextDefaults.Normalize();
        LineDefaults ??= new MapEditorLineDefaults();
        LineDefaults.Normalize();
        ConcealDefaults ??= new MapEditorConcealDefaults();
        ConcealDefaults.Normalize();
    }
}

public sealed class MapEditorConcealDefaults
{
    public MapBackgroundLayerShape Shape { get; set; } = MapBackgroundLayerShape.Circle;
    public int BrushSizePixels { get; set; } = MapBackgroundProcessor.DefaultBrushSizePixels;

    public MapEditorConcealDefaults Clone() => new()
    {
        Shape = Shape,
        BrushSizePixels = BrushSizePixels
    };

    public void Normalize()
    {
        if (!Enum.IsDefined(Shape))
            Shape = MapBackgroundLayerShape.Circle;
        BrushSizePixels = Math.Clamp(
            BrushSizePixels <= 0 ? MapBackgroundProcessor.DefaultBrushSizePixels : BrushSizePixels,
            MapBackgroundProcessor.MinBrushSizePixels,
            MapBackgroundProcessor.MaxBrushSizePixels);
    }
}

/// <summary>Pure editor-tool state used by the WinUI surface and unit tests.</summary>
public sealed class MapEditorToolState
{
    public MapEditorTool ActiveTool { get; private set; } = MapEditorTool.Select;
    public string FirstFloorKey { get; set; } = "1f";
    public string ActiveFloorKey { get; set; } = "1f";
    public NormalizedRectangle? PendingMainGate { get; private set; }
    public bool UsesPrimaryGatePair => string.Equals(
        ActiveFloorKey,
        FirstFloorKey,
        StringComparison.OrdinalIgnoreCase);

    public bool Select(MapEditorTool tool)
    {
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
        if (ActiveTool is not (MapEditorTool.Text or MapEditorTool.Line or MapEditorTool.Rectangle or MapEditorTool.Anchor or MapEditorTool.Conceal))
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

/// <summary>Pure conceal-stroke state machine used by the editor and tests.</summary>
public sealed class MapConcealStrokeBuilder
{
    private readonly List<NormalizedPoint> _points = [];
    private MapBackgroundLayerShape _shape;
    private int _brushSizePixels;
    private double _imageWidth = 1d;
    private double _imageHeight = 1d;

    public bool IsActive { get; private set; }
    public IReadOnlyList<NormalizedPoint> Points => _points;
    public MapBackgroundLayerShape Shape => _shape;
    public int BrushSizePixels => _brushSizePixels;

    public void Begin(
        NormalizedPoint point,
        MapBackgroundLayerShape shape,
        int brushSizePixels,
        double imageWidth,
        double imageHeight)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!point.IsValid)
            throw new ArgumentOutOfRangeException(nameof(point));
        _shape = Enum.IsDefined(shape) ? shape : MapBackgroundLayerShape.Circle;
        _brushSizePixels = Math.Clamp(brushSizePixels, 1, 1024);
        _imageWidth = Math.Max(1d, imageWidth);
        _imageHeight = Math.Max(1d, imageHeight);
        _points.Clear();
        _points.Add(point.Clone());
        IsActive = true;
    }

    public void AddPoint(NormalizedPoint point)
    {
        if (!IsActive || point is null || !point.IsValid)
            return;
        var previous = _points[^1];
        var dx = (point.X - previous.X) * _imageWidth;
        var dy = (point.Y - previous.Y) * _imageHeight;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var step = Math.Max(1d, _brushSizePixels / 4d);
        var count = Math.Max(1, (int)Math.Ceiling(distance / step));
        for (var index = 1; index <= count; index++)
        {
            var ratio = index / (double)count;
            _points.Add(new NormalizedPoint
            {
                X = Math.Clamp(previous.X + ((point.X - previous.X) * ratio), 0d, 1d),
                Y = Math.Clamp(previous.Y + ((point.Y - previous.Y) * ratio), 0d, 1d)
            });
        }
    }

    public MapBackgroundLayer? Complete()
    {
        if (!IsActive || _points.Count == 0)
        {
            Cancel();
            return null;
        }
        var layer = new MapBackgroundLayer
        {
            Id = Guid.NewGuid(),
            Semantic = "background",
            Shape = _shape,
            BrushSizePixels = _brushSizePixels,
            Points = _points.Select(point => point.Clone()).ToList()
        };
        Cancel();
        return layer;
    }

    public void Cancel()
    {
        IsActive = false;
        _points.Clear();
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
/*
 * 文件职责：MapEditorState。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
