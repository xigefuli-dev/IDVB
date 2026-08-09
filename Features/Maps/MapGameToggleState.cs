namespace IDVBuff.Features.Maps;

public readonly record struct MapGameToggleTransition(bool IsOpen, int Version);

/// <summary>
/// Tracks the expected state of the game's own map while rejecting stale
/// delayed-open work after a newer open/close input.
/// </summary>
public sealed class MapGameToggleState
{
    private int _openPipelineVersion = -1;

    public bool IsOpen { get; private set; }
    public int Version { get; private set; }

    public MapGameToggleTransition Toggle()
    {
        IsOpen = !IsOpen;
        Version++;
        if (!IsOpen)
            _openPipelineVersion = -1;
        return new MapGameToggleTransition(IsOpen, Version);
    }

    public void MarkOpen()
    {
        IsOpen = true;
        Version++;
        // An explicit scan already owns the scan/alignment pipeline for this
        // open map. The game-map binding must only close it next.
        _openPipelineVersion = Version;
    }

    /// <summary>
    /// Synchronizes the runtime state with an externally controlled game map.
    /// This is used by the real CLI after overlay_game has sent the same
    /// XButton1 event that a player would send.  Unlike <see cref="MarkOpen"/>
    /// it leaves the open pipeline available for the explicit align command.
    /// </summary>
    public MapGameToggleTransition SetOpenForExternalController(bool isOpen)
    {
        IsOpen = isOpen;
        Version++;
        _openPipelineVersion = -1;
        return new MapGameToggleTransition(IsOpen, Version);
    }

    public void Reset()
    {
        IsOpen = false;
        Version++;
        _openPipelineVersion = -1;
    }

    /// <summary>
    /// Releases the claimed alignment pipeline while keeping the game's map
    /// logically open. A failed alignment must not poison the current open
    /// transition; the next close/reopen cycle can claim a fresh pipeline.
    /// </summary>
    public void ReleaseOpenPipeline() => _openPipelineVersion = -1;

    public bool IsCurrent(MapGameToggleTransition transition) =>
        transition.Version == Version && transition.IsOpen == IsOpen;

    /// <summary>
    /// Claims the one automatic scan/alignment pipeline allowed for an open
    /// transition. Passive monitoring and stale async continuations cannot
    /// claim it again.
    /// </summary>
    public bool TryBeginOpenPipeline(MapGameToggleTransition transition)
    {
        if (!transition.IsOpen
            || !IsCurrent(transition)
            || _openPipelineVersion == transition.Version)
        {
            return false;
        }

        _openPipelineVersion = transition.Version;
        return true;
    }
}
