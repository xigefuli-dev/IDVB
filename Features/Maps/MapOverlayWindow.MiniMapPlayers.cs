using System.Drawing;

namespace IDVBuff.Features.Maps;

public sealed partial class MapOverlayWindow
{
    private readonly object _miniMapPlayersGate = new();
    private readonly Dictionary<PlayerSlot, MiniMapTrackedPlayer> _miniMapTrackedPlayers = new();

    internal IReadOnlyList<MiniMapTrackedPlayer> GetCurrentMiniMapPlayers()
    {
        lock (_miniMapPlayersGate)
        {
            if (_miniMapTrackedPlayers.Count == 0)
                return Array.Empty<MiniMapTrackedPlayer>();

            return _miniMapTrackedPlayers.Values.ToArray();
        }
    }

    /// <summary>
    /// 更新小地图上的多玩家实时位置点位。
    /// </summary>
    public void UpdateMiniMapPlayers(IReadOnlyList<MiniMapTrackedPlayer> players)
    {
        if (players is null || players.Count == 0) return;
        var changed = false;
        lock (_miniMapPlayersGate)
        {
            foreach (var p in players)
            {
                if (!_miniMapTrackedPlayers.TryGetValue(p.Slot, out var existing)
                    || Math.Abs(existing.NormalizedX - p.NormalizedX) > 0.001
                    || Math.Abs(existing.NormalizedY - p.NormalizedY) > 0.001)
                {
                    _miniMapTrackedPlayers[p.Slot] = p;
                    changed = true;
                }
            }
        }

        if (changed && IsVisible && _persistentMiniMap is not null)
        {
            Present();
        }
    }

    /// <summary>
    /// 清除小地图上记录的玩家点位。
    /// </summary>
    public void ClearMiniMapPlayers()
    {
        lock (_miniMapPlayersGate)
        {
            if (_miniMapTrackedPlayers.Count == 0) return;
            _miniMapTrackedPlayers.Clear();
        }

        if (IsVisible && _persistentMiniMap is not null)
        {
            Present();
        }
    }
}
