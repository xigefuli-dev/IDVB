namespace IDVBuff.Features.Maps;

public enum PlayerSlot
{
    Player1 = 1,
    Player2 = 2,
    Player3 = 3,
    Player4 = 4
}

public enum MapMatchState
{
    Ended,
    Started
}

public enum MapRunMode
{
    Normal,
    Survey
}

public sealed record MapVariantOption(
    Guid MapId,
    int VariantNumber,
    int SequenceNumber,
    string MapName,
    bool IsCurrent,
    bool IsPending);

public sealed record MapVariantSelectionContext(
    Guid GroupId,
    IReadOnlyList<MapVariantOption> Options);

public readonly record struct MapMatchSnapshot(
    MapMatchState State,
    PlayerSlot? PlayerSlot,
    int Version,
    string? MapClass = null,
    Guid MatchId = default,
    MapRunMode Mode = MapRunMode.Normal,
    Guid? SurveyProjectId = null,
    string? FloorKey = null)
{
    public long OperationEpoch => Version;

    public bool IsStarted => State == MapMatchState.Started;
}

/// <summary>
/// Owns the process-local match state. The version rejects work that was
/// started by an older match after the user ends or restarts a match.
/// </summary>
public sealed class MapMatchSession
{
    public MapMatchSnapshot Snapshot { get; private set; } =
        new(MapMatchState.Ended, null, 0);

    public MapMatchSnapshot Begin(string mapClass = "S1")
    {
        if (Snapshot.IsStarted)
            throw new InvalidOperationException("A match is already in progress.");
        if (string.IsNullOrWhiteSpace(mapClass))
            throw new ArgumentException("A map class is required.", nameof(mapClass));

        Snapshot = new MapMatchSnapshot(
            MapMatchState.Started,
            null,
            Snapshot.Version + 1,
            mapClass.Trim(),
            Guid.NewGuid(),
            MapRunMode.Normal,
            null,
            null);
        return Snapshot;
    }

    [Obsolete("Player slots are no longer used. Call Begin(mapClass).")]
    public MapMatchSnapshot Begin(PlayerSlot playerSlot, string mapClass = "S1") =>
        Begin(mapClass);

    public MapMatchSnapshot AdvanceOperationEpoch()
    {
        if (!Snapshot.IsStarted)
            throw new InvalidOperationException("No match is in progress.");
        Snapshot = Snapshot with { Version = Snapshot.Version + 1, PlayerSlot = null };
        return Snapshot;
    }

    public MapMatchSnapshot SwitchToSurvey(Guid projectId)
    {
        if (!Snapshot.IsStarted)
            throw new InvalidOperationException("No match is in progress.");
        if (projectId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(projectId));
        Snapshot = Snapshot with
        {
            Version = Snapshot.Version + 1,
            Mode = MapRunMode.Survey,
            SurveyProjectId = projectId
        };
        return Snapshot;
    }

    public MapMatchSnapshot ChangeFloor(string floorKey)
    {
        if (!Snapshot.IsStarted)
            throw new InvalidOperationException("No match is in progress.");
        if (string.IsNullOrWhiteSpace(floorKey))
            throw new ArgumentException("A floor key is required.", nameof(floorKey));
        Snapshot = Snapshot with
        {
            Version = Snapshot.Version + 1,
            FloorKey = floorKey.Trim().ToLowerInvariant()
        };
        return Snapshot;
    }

    public MapMatchSnapshot End()
    {
        Snapshot = new MapMatchSnapshot(
            MapMatchState.Ended,
            null,
            Snapshot.Version + 1,
            null,
            Guid.Empty,
            MapRunMode.Normal,
            null,
            null);
        return Snapshot;
    }

    public bool IsCurrent(MapMatchSnapshot snapshot) =>
        snapshot.Version == Snapshot.Version
        && snapshot.State == Snapshot.State
        && snapshot.MatchId == Snapshot.MatchId
        && snapshot.Mode == Snapshot.Mode
        && snapshot.SurveyProjectId == Snapshot.SurveyProjectId
        && string.Equals(snapshot.FloorKey, Snapshot.FloorKey, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            snapshot.MapClass,
            Snapshot.MapClass,
            StringComparison.OrdinalIgnoreCase);
}

public static class MapPlayerAssetCatalog
{
    public static IReadOnlyList<PlayerSlot> Slots { get; } =
    [
        PlayerSlot.Player1,
        PlayerSlot.Player2,
        PlayerSlot.Player3,
        PlayerSlot.Player4
    ];

    public static string FileNameFor(PlayerSlot playerSlot) => playerSlot switch
    {
        PlayerSlot.Player1 => "player_01.png",
        PlayerSlot.Player2 => "player_02.png",
        PlayerSlot.Player3 => "player_03.png",
        PlayerSlot.Player4 => "player_04.png",
        _ => throw new ArgumentOutOfRangeException(nameof(playerSlot))
    };

    public static string ResolvePath(PlayerSlot playerSlot)
    {
        var fileName = FileNameFor(playerSlot);
        var deployed = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            fileName);
        if (File.Exists(deployed))
            return deployed;

        var project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Assets",
            fileName));
        if (File.Exists(project))
            return project;

        var current = Path.Combine(
            Environment.CurrentDirectory,
            "Assets",
            fileName);
        return File.Exists(current) ? current : deployed;
    }

    public static bool AreAllAvailable =>
        Slots.All(slot => File.Exists(ResolvePath(slot)));
}
/*
 * 文件职责：MapPlayerModels。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
