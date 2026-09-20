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

    [JsonIgnore]
    public bool IsIdentityLocked =>
        MapId is not null
        && State is MapSessionState.Confirming or MapSessionState.Locked;
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

    public MapSessionSnapshot BeginVariantChange(Guid mapId, string floor)
    {
        if (mapId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(mapId));
        if (string.IsNullOrWhiteSpace(floor))
            throw new ArgumentException("A floor is required.", nameof(floor));
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            MapId = mapId,
            Floor = floor,
            State = MapSessionState.RecalibrationRequired,
            RecalibrationReason = MapRecalibrationReason.VariantChanged,
            Detail = "variant changed; waiting for an independent alignment"
        };
        return Snapshot;
    }

    public MapSessionSnapshot RetargetVariantFloor(Guid mapId, string floor)
    {
        if (Snapshot.State != MapSessionState.RecalibrationRequired
            || Snapshot.RecalibrationReason != MapRecalibrationReason.VariantChanged
            || Snapshot.MapId != mapId)
        {
            throw new InvalidOperationException(
                "Only the pending variant identity may change floors before alignment.");
        }

        return BeginVariantChange(mapId, floor);
    }

    public MapSessionSnapshot LockAlignedMap(
        Guid mapId,
        string floor,
        MapSimilarityTransform transform,
        MapLocationMethod locationMethod,
        double confidence)
    {
        if (mapId == Guid.Empty || !transform.IsValid)
            throw new ArgumentException("A valid map identity and transform are required.");
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            MapId = mapId,
            Floor = floor,
            State = MapSessionState.Locked,
            LocationMethod = locationMethod,
            LockedTransform = transform,
            Confidence = Math.Clamp(confidence, 0d, 1d),
            StableCandidateFrames = 1,
            Detail = "independent alignment committed"
        };
        return Snapshot;
    }

    /// <summary>
    /// Commits an explicit user map choice immediately, before a trustworthy
    /// screen transform is available. Identity lock and alignment lock are
    /// intentionally separate: a rejected first alignment must not undo the
    /// map selected by the user.
    /// </summary>
    public MapSessionSnapshot LockMapIdentity(
        Guid mapId,
        string floor,
        double confidence)
    {
        if (mapId == Guid.Empty)
            throw new ArgumentOutOfRangeException(nameof(mapId));
        if (string.IsNullOrWhiteSpace(floor))
            throw new ArgumentException("A floor is required.", nameof(floor));

        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            MapId = mapId,
            Floor = floor,
            State = MapSessionState.Confirming,
            LocationMethod = MapLocationMethod.None,
            Confidence = Math.Clamp(
                double.IsFinite(confidence) ? confidence : 0d,
                0d,
                1d),
            Detail = "user-selected map identity locked; alignment pending"
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
/*
 * 文件职责：MapSessionModels.Session。
 * 所属模块：Features/Maps，主要负责地图识别、对齐、会话编排、缓存或覆盖层功能。
 * 设计说明：本文件承载一个相对独立的实现片段；它通过公开类型、方法或 partial 类型与同模块的其他文件协作，避免把完整地图流程集中在单个超大文件中。
 * 数据流：输入通常来自截图、识别结果、会话状态、配置或持久化缓存；输出应继续交给识别、对齐、渲染、日志或发布流程使用。调用方应遵守类型契约，并注意空值、超时、置信度和取消状态。
 * 维护约束：这里只补充说明，不改变业务逻辑。涉及楼层尺度时必须保持楼层之间完全独立；涉及 UI、窗口句柄或系统资源时应遵守生命周期与释放约定；调整算法时应同步检查相关规则、诊断和测试。
 */
