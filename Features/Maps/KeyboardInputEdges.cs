namespace IDVBuff.Features.Maps;

/// <summary>Reconciles hook edges with asynchronous keyboard state snapshots.
/// The caller serializes both sampling and mutation with its keyboard lock.</summary>
internal sealed class KeyboardInputEdges
{
    // A low-level hook runs before Windows updates GetAsyncKeyState. Ignore
    // contradictory snapshots briefly, not subsequent real press/release edges.
    private const long HookStateCatchUpMilliseconds = 120;
    private readonly HashSet<uint> _pressed = [];
    private readonly Dictionary<uint, (bool Down, long At)> _pendingHookState = [];
    private readonly HashSet<uint> _hostReleased = [];

    public void InitializePressed(uint key) => _pressed.Add(key);

    public void IgnoreHostRelease(uint key)
    {
        // SendInput changes asynchronous state even though the user may still
        // physically hold the key. Only the real hook release can rearm it.
        if (_pressed.Contains(key)) _hostReleased.Add(key);
    }

    public bool Observe(uint key, bool down, bool polling, long now)
    {
        if (polling)
        {
            if (_hostReleased.Contains(key)) return false;
            if (_pendingHookState.TryGetValue(key, out var pending))
            {
                if (pending.Down != down && now - pending.At < HookStateCatchUpMilliseconds)
                    return false;
                _pendingHookState.Remove(key);
            }
        }
        else
        {
            _pendingHookState[key] = (down, now);
            if (!down) _hostReleased.Remove(key);
        }
        return down ? _pressed.Add(key) : _pressed.Remove(key);
    }

    public void Clear()
    {
        _pressed.Clear();
        _pendingHookState.Clear();
        _hostReleased.Clear();
    }
}
