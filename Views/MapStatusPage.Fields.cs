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
    private sealed class SliderSaveState
    {
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private readonly SessionOrchestrator _runtime = App.Session;
    private readonly ToggleSwitch _enabledToggle = new()
    {
        Header = "总开关",
        OffContent = "已关闭",
        OnContent = "已启动"
    };
    private readonly ToggleSwitch _firstScanStrategyToggle = new()
    {
        Header = "首次扫描策略",
        OffContent = "双门对齐",
        OnContent = "侧门扫描（默认）"
    };
    private readonly ComboBox _presetSelector = new()
    {
        Header = "使用配置文件",
        MinWidth = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
        DisplayMemberPath = "Name"
    };
    private readonly ToggleSwitch _overlayStatusToggle = new()
    {
        Header = "状态显示模式",
        OffContent = "交替显示",
        OnContent = "同时显示"
    };
    private readonly ToggleSwitch _reverseAlternateDisplayToggle = new()
    {
        Header = "反向交替显示",
        OffContent = "关闭（无地图时显示状态）",
        OnContent = "开启（有地图时显示状态）"
    };
    private ToggleSwitch _collectLogsToggle = new()
    {
        Header = "收集日志",
        OffContent = "已关闭",
        OnContent = "正在收集"
    };
    private readonly ToggleSwitch _collectResearchToggle = new()
    {
        Header = "算法研究数据采集",
        OffContent = "已关闭",
        OnContent = "正在采集"
    };
    private readonly ToggleSwitch _allowExtendToggle = new()
    {
        Header = "允许显示超出边界",
        OffContent = "裁剪至校准区域",
        OnContent = "允许超出"
    };
    private readonly ToggleSwitch _miniMapEnabledToggle = new()
    {
        Header = "常驻显示小地图",
        OffContent = "跟随游戏地图",
        OnContent = "始终显示"
    };
    private readonly ToggleSwitch _playerTrackingToggle = new()
    {
        Header = "追踪玩家位置",
        OffContent = "已关闭",
        OnContent = "正在追踪"
    };
    private readonly ToggleSwitch _showGateMarkersToggle = new()
    {
        Header = "大门与侧门标记",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showAuxiliaryAnchorsToggle = new()
    {
        Header = "辅助锚点",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showTextAnnotationsToggle = new()
    {
        Header = "注释文字",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showBoxAnnotationsToggle = new()
    {
        Header = "标注框线",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showLineAnnotationsToggle = new()
    {
        Header = "直线标注",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly Slider _mapOpacitySlider = CreatePercentageSlider("大地图不透明度");
    private readonly TextBlock _mapOpacityValue = CreateMutedText();
    private readonly ToggleSwitch _showGateMarkersOnMiniMapToggle = new()
    {
        Header = "大门与侧门标记",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showAuxiliaryAnchorsOnMiniMapToggle = new()
    {
        Header = "辅助锚点",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showTextAnnotationsOnMiniMapToggle = new()
    {
        Header = "注释文字",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showBoxAnnotationsOnMiniMapToggle = new()
    {
        Header = "标注框线",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showLineAnnotationsOnMiniMapToggle = new()
    {
        Header = "直线标注",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly ToggleSwitch _showFloorOnMiniMapToggle = new()
    {
        Header = "显示所在楼层",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly Slider _statusOpacitySlider = CreatePercentageSlider("状态不透明度");
    private readonly TextBlock _statusOpacityValue = CreateMutedText();
    private readonly Slider _statusOffsetXSlider = CreateOffsetSlider("状态 X 偏移 (px)");
    private readonly TextBlock _statusOffsetXValue = CreateMutedText();
    private readonly Slider _statusOffsetYSlider = CreateOffsetSlider("状态 Y 偏移 (px)");
    private readonly TextBlock _statusOffsetYValue = CreateMutedText();
    private readonly Slider _miniMapOpacitySlider = CreatePercentageSlider("小地图不透明度");
    private readonly TextBlock _miniMapOpacityValue = CreateMutedText();
    private readonly Slider _miniMapOffsetXSlider = CreateOffsetSlider("小地图 X 偏移 (px)");
    private readonly TextBlock _miniMapOffsetXValue = CreateMutedText();
    private readonly Slider _miniMapOffsetYSlider = CreateOffsetSlider("小地图 Y 偏移 (px)");
    private readonly TextBlock _miniMapOffsetYValue = CreateMutedText();
    private readonly Slider _miniMapScaleSlider = new()
    {
        Header = "小地图缩放",
        Minimum = 10,
        Maximum = 100,
        StepFrequency = 1,
        TickFrequency = 10,
        IsThumbToolTipEnabled = true,
        MinWidth = 300,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly TextBlock _miniMapScaleValue = CreateMutedText();
    private CancellationTokenSource? _miniMapScaleSaveCancellation;
    private readonly SliderSaveState _statusOpacitySave = new();
    private readonly SliderSaveState _statusOffsetXSave = new();
    private readonly SliderSaveState _statusOffsetYSave = new();
    private readonly SliderSaveState _mapOpacitySave = new();
    private readonly SliderSaveState _miniMapOpacitySave = new();
    private readonly SliderSaveState _miniMapOffsetXSave = new();
    private readonly SliderSaveState _miniMapOffsetYSave = new();
    private readonly ComboBox _alignmentMode = new()
    {
        Header = "图层对齐模式",
        MinWidth = 300,
        HorizontalAlignment = HorizontalAlignment.Left,
        ItemsSource = new[]
        {
            new AlignmentModeChoice(
                MapOverlayAlignmentMode.Uniform,
                "等比缩放＋固定旋转＋屏幕平移")
        },
        DisplayMemberPath = nameof(AlignmentModeChoice.DisplayName)
    };
    private readonly TextBlock _gameMapBinding = CreateMutedText();
    private readonly TextBlock _controlPanelBinding = CreateMutedText();
    private readonly TextBlock _quickBinding = CreateMutedText();
    private readonly TextBlock _overlayBinding = CreateMutedText();
    private readonly TextBlock _manualBinding = CreateMutedText();
    private readonly TextBlock _switchFloorBinding = CreateMutedText();
    private readonly TextBlock _saveMapCacheBinding = CreateMutedText();
    private readonly ToggleSwitch _allowAutomaticMapCacheToggle = new()
    {
        Header = "允许自动保存地图缓存",
        OffContent = "关闭",
        OnContent = "退出对局时询问是否保存"
    };
    private readonly TextBlock _overlayState = CreateMutedText();
    private readonly TextBlock _lastResult = CreateMutedText();
    private readonly TextBlock _status = CreateMutedText();
    private readonly TextBlock _calibrationState = CreateMutedText();
    private readonly TextBlock _floorCalibrationState = CreateMutedText();
    private readonly TextBlock _playerCalibrationState = CreateMutedText();
    private readonly TextBlock _floorState = CreateMutedText();
    private readonly TextBlock _mapReadiness = CreateMutedText();
    private readonly TextBlock _selectedMapState = CreateMutedText();
    private readonly TextBlock _alignmentState = CreateMutedText();
    private readonly TextBlock _sessionState = CreateMutedText();
    private readonly TextBlock _matchState = CreateMutedText();
    private readonly TextBlock _controlPanelState = CreateMutedText();
    private readonly TextBlock _playerState = CreateMutedText();
    private readonly TextBlock _timings = CreateMutedText();
    private readonly TextBlock _logState = CreateMutedText();
    private readonly TextBlock _researchState = CreateMutedText();
    private readonly TextBlock _permissionState = CreateMutedText();
    private readonly NumberBox _gateThreshold = CreatePercentageBox(
        "门模板最低分",
        50,
        95);
    private readonly NumberBox _sideEntranceFeatureRadius = CreateDecimalBox(
        "侧门特征半径（px）",
        20,
        500,
        10);
    private readonly NumberBox _minimumConfidence = CreatePercentageBox(
        "最终识别最低置信度",
        20,
        95);
    private readonly NumberBox _mediumConfidenceThreshold = CreatePercentageBox(
        "地图解锁安全置信度阈值",
        30,
        98);
    private readonly NumberBox _highConfidenceThreshold = CreatePercentageBox(
        "高置信度直接锁定阈值",
        65,
        99);
    private readonly NumberBox _openingAnimationDelay = CreateDecimalBox(
        "\u5F00\u56FE\u52A8\u753B\u7B49\u5F85\uFF08ms\uFF09",
        0,
        1500,
        10);
    private readonly NumberBox _openingTimeout = CreateDecimalBox(
        "开图超时（ms）",
        1000,
        10000,
        100);
    private readonly NumberBox _stableFrameCount = CreateDecimalBox(
        "稳定帧数量",
        2,
        8,
        1);
    private readonly NumberBox _stableFrameInterval = CreateDecimalBox(
        "稳定帧间隔（ms）",
        20,
        250,
        10);
    private readonly NumberBox _stableFrameDifference = CreateDecimalBox(
        "稳定判定差异",
        0.001,
        0.10,
        0.001);
    private readonly NumberBox _mediumConfidenceFrames = CreateDecimalBox(
        "中等置信度确认帧数",
        2,
        8,
        1);
    private readonly NumberBox _candidateStabilityPixels = CreateDecimalBox(
        "候选变换稳定像素",
        0.5,
        20,
        0.5);
    private readonly NumberBox _nativeScaleChangeRatio = CreatePercentageBox(
        "原生地图缩放变化阈值",
        1,
        20);
    private readonly NumberBox _vectorTolerance = CreateDecimalBox(
        "几何向量容差",
        0.01,
        0.15,
        0.005);
    private readonly NumberBox _ambiguityMargin = CreateDecimalBox(
        "歧义差距",
        0.001,
        0.05,
        0.001);
    private readonly NumberBox _confirmationAdvantage = CreateDecimalBox(
        "局部复核最低优势",
        0.01,
        0.25,
        0.01);
    private readonly NumberBox _warmGateSearchBudget = CreateDecimalBox(
        "门搜索预算（ms）",
        0,
        1000,
        50);
    private readonly NumberBox _confirmationGateSearchBudget = CreateDecimalBox(
        "复核搜索预算（ms）",
        0,
        500,
        25);
    private readonly NumberBox _confirmationRoiPaddingFactor = CreateDecimalBox(
        "复核 ROI 扩展比例",
        0.5,
        3,
        0.1);
    private readonly NumberBox _confirmationRoiMinimumPadding = CreateDecimalBox(
        "复核 ROI 最小边距（px）",
        8,
        100,
        4);
    private readonly NumberBox _confirmationMaximumMapDrag = CreateDecimalBox(
        "最大拖动速度（px/s）",
        100,
        3000,
        50);
    private readonly NumberBox _floorMinimumConfidence = CreatePercentageBox(
        "楼层最终置信度",
        30,
        99);
    private readonly NumberBox _floorLocalizationConfidence = CreatePercentageBox(
        "楼层数字定位置信度",
        30,
        99);
    private readonly NumberBox _floorRecognitionTimeout = CreateDecimalBox(
        "楼层识别超时（ms）",
        500,
        10000,
        100);
    private readonly NumberBox _floorFirstConfirmationFrames = CreateDecimalBox(
        "1F 确认帧数",
        1,
        8,
        1);
    private readonly NumberBox _floorSecondConfirmationFrames = CreateDecimalBox(
        "2F 确认帧数",
        1,
        8,
        1);
    private readonly NumberBox _playerMinimumConfidence = CreatePercentageBox(
        "玩家最低置信度",
        30,
        99);
    private readonly NumberBox _playerFailureLimit = CreateDecimalBox(
        "局部搜索失败次数",
        1,
        20,
        1);
    private readonly NumberBox _playerStaleHideMilliseconds = CreateDecimalBox(
        "玩家标记隐藏延迟（ms）",
        100,
        5000,
        100);
    private readonly ToggleSwitch _forceBestResultToggle = new()
    {
        Header = "手动识别始终取最高分",
        OffContent = "按置信度和歧义度判断",
        OnContent = "直接输出最高分匹配"
    };
    private readonly ToggleSwitch _forceCandidateToggle = new()
    {
        Header = "强制进入候选界面",
        OffContent = "算法确定时自动用最高分地图",
        OnContent = "无论如何都弹出候选供玩家选择"
    };
    private readonly ToggleSwitch _playerDecidesScaleToggle = new()
    {
        Header = "由玩家决定缩放值",
        OffContent = "扫描后直接使用算法缩放",
        OnContent = "每次扫描后弹出窗口由玩家调整"
    };
    private readonly ToggleSwitch _skipFloorRecognitionToggle = new()
    {
        Header = "跳过楼层识别（使用手动楼层）",
        OffContent = "截图识别 1F/2F",
        OnContent = "不识别，按手动切换结果对齐"
    };
    private readonly ToggleSwitch _skipStabilityConfirmationToggle = new()
    {
        Header = "跳过稳定确认（中等置信度直接接受）",
        OffContent = "等待连续帧确认",
        OnContent = "跳过确认，省去等待"
    };
    private readonly ToggleSwitch _structureDebugToggle = new()
    {
        Header = "保存结构配准调试图（会显著变慢）",
        OffContent = "关闭",
        OnContent = "写入 LocalAppData"
    };
    private readonly ToggleSwitch _structureEccToggle = new()
    {
        Header = "ECC 亚像素精修",
        OffContent = "关闭",
        OnContent = "开启（推荐，仅平移且限制在 ±3px）"
    };
    private readonly ToggleSwitch _auxiliaryAnchorToggle = new()
    {
        Header = "使用辅助锚点识别",
        OffContent = "关闭（不搜索用户辅助锚点）",
        OnContent = "开启（仅在结构候选歧义时自动消歧）"
    };
    private readonly ToggleSwitch _reusePreviousAlignmentToggle = new()
    {
        Header = "优先复用上次对齐结果",
        OffContent = "关闭（每次从全图开始搜索）",
        OnContent = "开启（按楼层局部跟踪，失败后单次全图恢复）"
    };
    private readonly NumberBox _previousAlignmentSearchRadius =
        CreateDecimalBox(
            "上次结果局部搜索半径（屏幕像素）",
            8,
            1000,
            8);
    private readonly NumberBox _structureChamfer = CreateDecimalBox(
        "结构最大平均距离（参考像素）",
        0.5,
        20,
        0.1);
    private readonly NumberBox _structureCoverage = CreatePercentageBox(
        "结构边缘最低覆盖率",
        10,
        98);
    private readonly NumberBox _structureMargin = CreatePercentageBox(
        "结构候选最低差距",
        1,
        80);
    private readonly NumberBox _auxiliaryMaxTemplates = CreateDecimalBox(
        "辅助锚点数量上限",
        1,
        8,
        1);
    private readonly NumberBox _auxiliaryDirectLockConfidence = CreatePercentageBox(
        "辅助直接锁定阈值",
        65,
        95);
    private readonly NumberBox _viewportEdgeMargin = CreatePercentageBox(
        "视口边缘外扩",
        0,
        30);
    private readonly ToggleSwitch _featureVotingToggle = new()
    {
        Header = "启用 AKAZE 特征投票",
        OffContent = "关闭",
        OnContent = "开启"
    };
    private readonly ToggleSwitch _fastAlignmentToggle = new()
    {
        Header = "启用快速粗搜索（含跟踪/受限路径）",
        OffContent = "关闭（使用完整 ORB 管线）",
        OnContent = "开启（4× 降采样 + Chamfer，所有路径接入）"
    };
    private readonly ToggleSwitch _visibleAwareToggle = new()
    {
        Header = "Visible-aware 结构搜索（实验）",
        OffContent = "关闭",
        OnContent = "开启（可能改变候选排序）"
    };
    private readonly ToggleSwitch _fastShadowToggle = new()
    {
        Header = "快速粗搜索 Shadow 模式",
        OffContent = "关闭",
        OnContent = "同时执行新旧双路径，仅对比日志"
    };
    private readonly NumberBox _featureRatioThreshold = CreatePercentageBox(
        "特征比率筛选",
        50,
        95);
    private readonly NumberBox _featureInlierTolerance = CreateDecimalBox(
        "内点容差（px）",
        1,
        30,
        0.5);
    private readonly NumberBox _featureMaxCandidates = CreateDecimalBox(
        "平移候选数上限",
        2,
        10,
        1);
    private readonly NumberBox _structureOccupancy = CreatePercentageBox(
        "结构占用率阈值",
        10,
        98);
    private readonly NumberBox _structurePartitions = CreateDecimalBox(
        "最低一致分区数",
        1,
        4,
        1);
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
    private readonly Button _elevationButton = new()
    {
        Content = "管理员重启",
        MinHeight = 38,
        HorizontalAlignment = HorizontalAlignment.Left,
        Visibility = Visibility.Collapsed
    };
    private Grid? _root;
    private MapRuntimeBindingTarget? _recording;
    private MapInputModifiers _recordingModifiers;
    private readonly Dictionary<MapRuntimeBindingTarget, Button> _bindingButtons = [];
    private readonly Dictionary<MapRuntimeBindingTarget, bool> _bindingButtonHovered = [];
    private bool _refreshing;
    private bool _subscribedToRuntime;
    private bool _viewBuilt;
}
