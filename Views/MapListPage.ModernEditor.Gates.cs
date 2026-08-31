using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private void CommitModernGate(NormalizedRectangle sourceBounds)
    {
        var recognitionBounds = ToModernRecognitionBounds(sourceBounds);
        if (recognitionBounds?.IsValid is not true)
        {
            SetModernStatus("门特征必须完全位于当前裁剪范围内。", true);
            RenderModernEditor();
            return;
        }

        var profile = GetActiveFloorProfile();
        if (!_modernToolState.UsesPrimaryGatePair)
        {
            CommitModernSecondaryGate(profile, recognitionBounds);
            return;
        }

        if (_modernToolState.PendingMainGate is null)
        {
            _modernToolState.StageMainGate(recognitionBounds);
            SetModernStatus("正门已暂存；请继续拖动标记侧门。", false);
            RenderModernEditor();
            return;
        }

        var transaction = _modernToolState.CommitSideGate(recognitionBounds);
        if (transaction is null)
            return;
        var main = profile.FindAnchor("main-entrance");
        var side = profile.FindAnchor("side-entrance");
        if (main is null || side is null)
        {
            SetModernStatus("门特征模型不完整，无法提交。", true);
            return;
        }

        var previousMainBounds = main.Bounds?.Clone();
        var previousSideBounds = side.Bounds?.Clone();
        var mainId = main.Id;
        var sideId = side.Id;
        main.Bounds = transaction.Value.Main;
        side.Bounds = transaction.Value.Side;
        RecordModernCreation("已撤销门特征创建。", () =>
        {
            var currentMain = profile.FindAnchor(mainId);
            var currentSide = profile.FindAnchor(sideId);
            if (currentMain is not null)
                currentMain.Bounds = previousMainBounds?.Clone();
            if (currentSide is not null)
                currentSide.Bounds = previousSideBounds?.Clone();
        });
        _modernSelection = new EditorSelection(EditorSelectionKind.Anchor, side.Id);
        SetModernStatus("正门与侧门已一起更新。", false);
        RefreshModernToolVisuals();
        UpdateMarkerConfirmState();
        RefreshModernLayerList();
        RenderModernEditor();
    }

    private void CommitModernSecondaryGate(
        FloorRecognitionProfile profile,
        NormalizedRectangle recognitionBounds)
    {
        var secondary = profile.FindAnchor(
            MapScanFloorRules.SecondaryGateAnchorKey);
        if (secondary is null)
        {
            SetModernStatus("次要门特征模型不完整，无法提交。", true);
            return;
        }

        var previousBounds = secondary.Bounds?.Clone();
        var secondaryId = secondary.Id;
        secondary.Bounds = recognitionBounds;
        RecordModernCreation("已撤销次要门特征创建。", () =>
        {
            var currentSecondary = profile.FindAnchor(secondaryId);
            if (currentSecondary is not null)
                currentSecondary.Bounds = previousBounds?.Clone();
        });
        _modernSelection = new EditorSelection(
            EditorSelectionKind.Anchor,
            secondary.Id);
        CompleteModernCreation("已更新次要门特征。", returnToSelect: true);
    }
}
