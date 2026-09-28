namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    public void ToggleAlignmentResultVisibility()
    {
        if (_disposed
            || _settings is not { IsEnabled: true }
            || !_matchSession.Snapshot.IsStarted)
        {
            return;
        }

        _alignmentResultHidden = !_alignmentResultHidden;
        _overlay.SetMapContentVisible(!_alignmentResultHidden);

        if (_alignmentResultHidden)
        {
            _statusMessage = "已临时隐藏大地图贴合结果；后台贴合与拖动跟踪仍在继续。";
        }
        else if (_gameMapToggleState.IsOpen && _overlay.HasMap)
        {
            // SetMapContentVisible has re-presented the latest in-memory
            // transform. Do not enter alignment or advance map-toggle state.
            _statusMessage = "已显示当前大地图贴合结果。";
        }
        else
        {
            _statusMessage = _gameMapToggleState.IsOpen
                ? "已恢复贴合结果显示；等待当前地图产生可显示结果。"
                : "已恢复贴合结果显示；下次打开游戏地图时生效。";
        }

        _logCollector.Append(
            MapLogCategory.Overlay,
            MapLogLevel.Info,
            _alignmentResultHidden
                ? "大地图贴合结果已临时隐藏"
                : "大地图贴合结果已恢复显示",
            details: new()
            {
                ["hidden"] = _alignmentResultHidden,
                ["gameMapOpen"] = _gameMapToggleState.IsOpen,
                ["hasMap"] = _overlay.HasMap
            });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
