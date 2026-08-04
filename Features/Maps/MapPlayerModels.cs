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

public readonly record struct MapMatchSnapshot(
    MapMatchState State,
    PlayerSlot? PlayerSlot,
    int Version,
    string? MapClass = null)
{
    public bool IsStarted =>
        State == MapMatchState.Started
        && PlayerSlot is { } slot
        && Enum.IsDefined(slot);
}

/// <summary>
/// Owns the process-local match state. The version rejects work that was
/// started by an older match after the user ends or restarts a match.
/// </summary>
public sealed class MapMatchSession
{
    public MapMatchSnapshot Snapshot { get; private set; } =
        new(MapMatchState.Ended, null, 0);

    public MapMatchSnapshot Begin(PlayerSlot playerSlot, string mapClass = "S1")
    {
        if (!Enum.IsDefined(playerSlot))
            throw new ArgumentOutOfRangeException(nameof(playerSlot));
        if (Snapshot.IsStarted)
            throw new InvalidOperationException("A match is already in progress.");
        if (string.IsNullOrWhiteSpace(mapClass))
            throw new ArgumentException("A map class is required.", nameof(mapClass));

        Snapshot = new MapMatchSnapshot(
            MapMatchState.Started,
            playerSlot,
            Snapshot.Version + 1,
            mapClass.Trim());
        return Snapshot;
    }

    public MapMatchSnapshot End()
    {
        Snapshot = new MapMatchSnapshot(
            MapMatchState.Ended,
            null,
            Snapshot.Version + 1,
            null);
        return Snapshot;
    }

    public bool IsCurrent(MapMatchSnapshot snapshot) =>
        snapshot.Version == Snapshot.Version
        && snapshot.State == Snapshot.State
        && snapshot.PlayerSlot == Snapshot.PlayerSlot
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
