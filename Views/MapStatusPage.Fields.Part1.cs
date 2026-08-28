using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;
public sealed partial class MapStatusPage : UserControl
{
    private readonly NumberBox _structureEdgeTolerance = CreateDecimalBox(
        "边缘距离容差（px）",
        0.5,
        8,
        0.25);
    private readonly NumberBox _structureTopCandidates = CreateDecimalBox(
        "候选数量上限",
        2,
        20,
        1);
    private readonly NumberBox _structureBudget = CreateDecimalBox(
        "搜索时间预算（ms）",
        250,
        5000,
        50);
    private readonly NumberBox _fastCoarseDownsample = CreateDecimalBox(
        "粗搜索降采样",
        1,
        8,
        1);
    private readonly NumberBox _fastCoarseTopK = CreateDecimalBox(
        "粗搜索 Top-K",
        1,
        20,
        1);
    private readonly Button _scanButton = CreateActionButton("3 秒后扫描");
    private readonly Button _manualButton = CreateActionButton("3 秒后手动识别");
    private Grid? _root;
    private MapRuntimeBindingTarget? _recording;
    private MapInputModifiers _recordingModifiers;
    private readonly Dictionary<MapRuntimeBindingTarget, Button> _bindingButtons = [];
    private readonly Dictionary<MapRuntimeBindingTarget, UIElement> _bindingRows = [];
    private readonly Dictionary<MapRuntimeBindingTarget, bool> _bindingButtonHovered = [];
    private bool _refreshing;
    private bool _subscribedToRuntime;
    private bool _viewBuilt;
}
