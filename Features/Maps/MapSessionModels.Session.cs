using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public sealed class MapSessionSnapshot
{
    public int Version { get; init; }
    /// <summary>
    /// Changes only when the alignment lock itself is created, updated,
    /// invalidated, or closed. Player-only updates preserve this value.
    /// </summary>
    public long AlignmentRevision { get; init; }
    public Guid? MapId { get; init; }
    public string? Floor { get; init; }
    public MapSessionState State { get; init; } = MapSessionState.Closed;
    public MapLocationMethod LocationMethod { get; init; }
    public MapRecalibrationReason RecalibrationReason { get; init; }
    public MapViewportOrigin? ViewportOrigin { get; init; }
    public MapSimilarityTransform? LockedTransform { get; init; }
    public MapPlayerState? Player { get; init; }
    public double Confidence { get; init; }
    public int StableCandidateFrames { get; init; }
    public string Detail { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsLocked =>
        State == MapSessionState.Locked
        && LockedTransform?.IsValid is true;
}

public sealed class MapOpenSession
{
    private int _version;
    private long _alignmentRevision;

    public MapSessionSnapshot Snapshot { get; private set; } = new();

    public MapSessionSnapshot Transition(
        MapSessionState state,
        Guid? mapId = null,
        string? floor = null,
        MapLocationMethod locationMethod = MapLocationMethod.None,
        MapRecalibrationReason reason = MapRecalibrationReason.None,
        MapViewportOrigin? viewportOrigin = null,
        MapSimilarityTransform? lockedTransform = null,
        MapPlayerState? player = null,
        double confidence = 0d,
        int stableCandidateFrames = 0,
        string? detail = null)
    {
        if (!MapSessionRules.IsValidTransition(Snapshot.State, state))
        {
            throw new InvalidOperationException(
                $"Invalid map session transition: {Snapshot.State} -> {state}.");
        }

        var resolvedMapId = mapId ?? Snapshot.MapId;
        var resolvedFloor = floor ?? Snapshot.Floor;
        if (Snapshot.IsLocked
            && state == MapSessionState.Locked
            && (resolvedMapId != Snapshot.MapId
                || !string.Equals(
                    resolvedFloor,
                    Snapshot.Floor,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "A locked map or floor cannot be replaced without entering recalibration first.");
        }
        var preservesLockedPlayer = state == MapSessionState.Locked
            && Snapshot.IsLocked
            && resolvedMapId == Snapshot.MapId
            && string.Equals(
                resolvedFloor,
                Snapshot.Floor,
                StringComparison.Ordinal);
        var resolvedLockedTransform = state == MapSessionState.Locked
            ? lockedTransform
                ?? (preservesLockedPlayer ? Snapshot.LockedTransform : null)
            : null;
        if (state == MapSessionState.Locked
            && resolvedLockedTransform?.IsValid is not true)
        {
            throw new InvalidOperationException(
                "Entering a locked state requires a newly validated transform.");
        }

        var changesAlignmentRevision = state == MapSessionState.Closed
            || Snapshot.IsLocked
            || state == MapSessionState.Locked;
        var alignmentRevision = changesAlignmentRevision
            ? ++_alignmentRevision
            : _alignmentRevision;
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = alignmentRevision,
            MapId = resolvedMapId,
            Floor = resolvedFloor,
            State = state,
            LocationMethod = locationMethod == MapLocationMethod.None
                ? Snapshot.LocationMethod
                : locationMethod,
            RecalibrationReason = reason,
            ViewportOrigin = state == MapSessionState.Locked
                ? viewportOrigin
                    ?? (preservesLockedPlayer ? Snapshot.ViewportOrigin : null)
                : null,
            LockedTransform = resolvedLockedTransform,
            Player = state == MapSessionState.Locked
                ? player ?? (preservesLockedPlayer ? Snapshot.Player : null)
                : null,
            Confidence = Math.Clamp(
                double.IsFinite(confidence) ? confidence : 0d,
                0d,
                1d),
            StableCandidateFrames = Math.Max(0, stableCandidateFrames),
            Detail = detail ?? string.Empty
        };
        return Snapshot;
    }

    public MapSessionSnapshot UpdatePlayer(MapPlayerState? player)
    {
        if (!Snapshot.IsLocked)
            return Snapshot;
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = Snapshot.AlignmentRevision,
            MapId = Snapshot.MapId,
            Floor = Snapshot.Floor,
            State = Snapshot.State,
            LocationMethod = Snapshot.LocationMethod,
            RecalibrationReason = Snapshot.RecalibrationReason,
            ViewportOrigin = Snapshot.ViewportOrigin,
            LockedTransform = Snapshot.LockedTransform,
            Player = player,
            Confidence = Snapshot.Confidence,
            StableCandidateFrames = Snapshot.StableCandidateFrames,
            Detail = Snapshot.Detail
        };
        return Snapshot;
    }

    /// <summary>
    /// Commits a newly trusted alignment observation without rebuilding the
    /// map-open session or discarding the current player observation.
    /// </summary>
    public MapSessionSnapshot UpdateLockedAlignment(
        Guid mapId,
        string floor,
        MapLocationMethod locationMethod,
        MapViewportOrigin viewportOrigin,
        MapSimilarityTransform lockedTransform,
        double confidence,
        int stableCandidateFrames,
        string? detail = null)
    {
        if (!Snapshot.IsLocked)
        {
            throw new InvalidOperationException(
                "Alignment observations can update only a locked map session.");
        }
        if (Snapshot.MapId != mapId
            || !string.Equals(Snapshot.Floor, floor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Alignment observations cannot change the locked map or floor.");
        }
        if (!lockedTransform.IsValid)
        {
            throw new InvalidOperationException(
                "The alignment observation transform is not valid.");
        }

        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            MapId = Snapshot.MapId,
            Floor = Snapshot.Floor,
            State = MapSessionState.Locked,
            LocationMethod = locationMethod == MapLocationMethod.None
                ? Snapshot.LocationMethod
                : locationMethod,
            RecalibrationReason = MapRecalibrationReason.None,
            ViewportOrigin = viewportOrigin,
            LockedTransform = lockedTransform,
            Player = Snapshot.Player,
            Confidence = Math.Clamp(
                double.IsFinite(confidence) ? confidence : 0d,
                0d,
                1d),
            StableCandidateFrames = Math.Max(0, stableCandidateFrames),
            Detail = detail ?? string.Empty
        };
        return Snapshot;
    }

    public MapSessionSnapshot Close(string? detail = null)
    {
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            State = MapSessionState.Closed,
            Detail = detail ?? string.Empty
        };
        return Snapshot;
    }
}

/// <summary>
/// Prevents an older dispatcher callback from rendering after a newer
/// alignment observation or session invalidation has already won.
/// </summary>
public sealed class MapAlignmentCommitGuard
{
    private readonly object _gate = new();
    private long _generation;

    public long BeginCommit()
    {
        lock (_gate)
            return ++_generation;
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
            return generation > 0 && _generation == generation;
    }

    public bool TryInvalidate(long generation)
    {
        lock (_gate)
        {
            if (generation <= 0 || _generation != generation)
                return false;
            _generation++;
            return true;
        }
    }

    public bool TryCommit(long generation, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (generation <= 0 || _generation != generation)
                return false;
            commit();
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
            _generation++;
    }
}
