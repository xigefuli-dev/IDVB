namespace IDVBuff.Features.Maps;

/// <summary>
/// 不可变开图与对齐操作上下文。
/// 在每次仅对齐/开图操作开始时捕获，并在截帧、自动楼层提议、多楼层试探和最终提交阶段
/// 集中用于版本与身份校验，阻止已过期的旧任务回写。
/// </summary>
public sealed record MapOpenOperationContext
{
    public required MapMatchSnapshot OperationMatch { get; init; }
    public required Guid MapId { get; init; }
    public required DateTimeOffset MapUpdatedAt { get; init; }
    public required int MapToggleVersion { get; init; }
    public required long OperationGeneration { get; init; }
    public string? ManualFloorKey { get; init; }
    public IntPtr WindowHandle { get; init; }
    public MapScreenRect ClientBounds { get; init; }
    public Guid CaptureFrameId { get; init; } = Guid.NewGuid();
    public CancellationToken CancellationToken { get; init; }

    public bool IsManualFloor => !string.IsNullOrEmpty(ManualFloorKey);
    public Guid MatchId => OperationMatch.MatchId;
    public long OperationEpoch => OperationMatch.Version;

    public bool MatchesCurrentOperation(
        MapMatchSnapshot currentMatch,
        bool isToggleOpen,
        int currentToggleVersion,
        long currentGeneration,
        Guid? currentMapId,
        DateTimeOffset? currentMapUpdatedAt,
        string? currentFloorKey = null)
    {
        if (CancellationToken.IsCancellationRequested)
            return false;

        if (currentMatch.State == MapMatchState.Ended
            || OperationMatch.MatchId != currentMatch.MatchId
            || OperationMatch.Version != currentMatch.Version)
        {
            return false;
        }

        if (!isToggleOpen || MapToggleVersion != currentToggleVersion)
            return false;

        if (OperationGeneration != currentGeneration)
            return false;

        if (currentMapId is null || MapId != currentMapId.Value)
            return false;

        if (currentMapUpdatedAt is not null && MapUpdatedAt != currentMapUpdatedAt.Value)
            return false;

        if (IsManualFloor && !string.Equals(ManualFloorKey, currentFloorKey, StringComparison.Ordinal))
            return false;

        return true;
    }
}

/// <summary>Latest-wins cancellation with an explicit continuing identity-commit owner.</summary>
internal sealed class MapOpenCancellationOwner
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;

    internal CancellationTokenSource Begin(CancellationToken match, CancellationToken external)
    {
        lock (_gate)
        {
            _current?.Cancel();
            var scope = CancellationTokenSource.CreateLinkedTokenSource(match, external);
            _current = scope;
            return scope;
        }
    }

    internal void Complete(CancellationTokenSource scope)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, scope)) _current = null;
        }
        scope.Dispose();
    }

    internal void CancelUnlessOwnedBy(CancellationToken continuingOwner = default)
    {
        lock (_gate)
        {
            if (continuingOwner.CanBeCanceled)
            {
                continuingOwner.ThrowIfCancellationRequested();
                if (_current is null || _current.Token != continuingOwner)
                    throw new OperationCanceledException("Map-open identity commit lost its owner.", continuingOwner);
                return;
            }
            _current?.Cancel();
        }
    }
}
