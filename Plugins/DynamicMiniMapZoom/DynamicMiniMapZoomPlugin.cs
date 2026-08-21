using IDVBuff.Core.Contracts;
using IDVBuff.PluginContracts;
using IDVBuff.PluginHostMessages;

namespace IDVBuff.Plugins.DynamicMiniMapZoom;

[Plugin(
    "dynamic-minimap-zoom",
    DisplayName = "动态小地图缩放",
    Description = "进入对局后按住 Caps 键滚动鼠标滚轮，临时调整小地图大小；结束对局后自动恢复。",
    Version = "1.0.0")]
public sealed class DynamicMiniMapZoomPlugin : PluginBase, IHandle<MatchStateChangedMessage>
{
    private IGlobalInput? _input;
    private IOverlayWindow? _overlay;
    private ISessionOrchestrator? _session;
    private bool _enabled;
    private bool _matchStarted;
    private string? _matchId;

    public override string Id => "dynamic-minimap-zoom";
    public override string DisplayName => "动态小地图缩放";

    public override void OnLoad(IPluginContext context)
    {
        base.OnLoad(context);
        _input = context.GetService<IGlobalInput>();
        _overlay = context.GetService<IOverlayWindow>();
        _session = context.GetService<ISessionOrchestrator>();

        if (_input is null)
            context.Logger.Error("无法取得全局输入服务，动态小地图缩放不可用。");
        if (_overlay is null)
            context.Logger.Error("无法取得叠加窗口服务，动态小地图缩放不可用。");
    }

    public override void OnEnable()
    {
        _enabled = true;
        _matchStarted = _session?.IsMatchStarted == true;
        _matchId = _session?.CurrentMatchId;
        _input?.MouseWheelScrolled += OnMouseWheelScrolled;
    }

    public override void OnDisable()
    {
        _enabled = false;
        if (_input is not null)
            _input.MouseWheelScrolled -= OnMouseWheelScrolled;
        EndTemporaryMatch();
    }

    public override void OnUnload()
    {
        _enabled = false;
        if (_input is not null)
            _input.MouseWheelScrolled -= OnMouseWheelScrolled;
        EndTemporaryMatch();
        _input = null;
        _overlay = null;
        _session = null;
    }

    public void Handle(MatchStateChangedMessage message)
    {
        if (string.Equals(message.State, "Started", StringComparison.OrdinalIgnoreCase))
        {
            if (!_matchStarted
                || !string.Equals(_matchId, message.MatchId, StringComparison.OrdinalIgnoreCase))
            {
                EndTemporaryMatch();
                _matchStarted = true;
                _matchId = message.MatchId;
            }

            return;
        }

        if (string.Equals(message.State, "Ended", StringComparison.OrdinalIgnoreCase))
            EndTemporaryMatch();
    }

    private void OnMouseWheelScrolled(object? sender, MouseWheelInputEventArgs args)
    {
        if (!_enabled || !_matchStarted || !args.CapsHeld || args.Delta == 0)
            return;

        if (_overlay?.CurrentMiniMapScale is not double currentScale)
            return;

        var nextScale = DynamicMiniMapZoomPolicy.Apply(
            currentScale,
            args.Delta);
        if (Math.Abs(nextScale - currentScale) <= 0.000001d)
            return;

        _overlay.SetMiniMapScale(nextScale);
    }

    private void EndTemporaryMatch()
    {
        _overlay?.ClearTemporaryMiniMapScales();

        _matchStarted = false;
        _matchId = null;
    }
}
