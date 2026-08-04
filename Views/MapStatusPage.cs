using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

/// <summary>Runtime control center for scanning, manual recognition, and overlay state.</summary>
public sealed class MapStatusPage : UserControl
{
    private sealed record AlignmentModeChoice(
        MapOverlayAlignmentMode Mode,
        string DisplayName);

    private readonly MapRuntimeService _runtime = MapRuntimeHost.Current;
    private readonly ToggleSwitch _enabledToggle = new()
    {
        Header = "解锁地图",
        OffContent = "已关闭",
        OnContent = "已启动"
    };
    private readonly ToggleSwitch _firstScanStrategyToggle = new()
    {
        Header = "首次扫描策略",
        OffContent = "双门对齐（默认）",
        OnContent = "侧门扫描（单侧门识别）"
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
    private readonly ToggleSwitch _showFloorOnMiniMapToggle = new()
    {
        Header = "显示所在楼层",
        OffContent = "隐藏",
        OnContent = "显示"
    };
    private readonly NumberBox _statusOpacityBox = CreatePercentageBox("状态不透明度", 0, 100);
    private readonly NumberBox _statusOffsetXBox = CreateDecimalBox("状态 X 偏移 (px)", -500, 500, 1);
    private readonly NumberBox _statusOffsetYBox = CreateDecimalBox("状态 Y 偏移 (px)", -500, 500, 1);
    private readonly NumberBox _miniMapOpacityBox = CreatePercentageBox("小地图不透明度", 0, 100);
    private readonly NumberBox _miniMapOffsetXBox = CreateDecimalBox("小地图 X 偏移 (px)", -500, 500, 1);
    private readonly NumberBox _miniMapOffsetYBox = CreateDecimalBox("小地图 Y 偏移 (px)", -500, 500, 1);
    private readonly NumberBox _miniMapScaleBox = CreatePercentageBox("小地图缩放", 10, 100);
    private readonly NumberBox _miniMapScale = CreatePercentageBox(
        "小地图缩放", 10, 100);
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
    private readonly ToggleSwitch _skipFloorRecognitionToggle = new()
    {
        Header = "跳过楼层识别（固定按 1F）",
        OffContent = "截图识别 1F/2F",
        OnContent = "跳过识别，省去 ~110-130ms"
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
        OnContent = "开启（仅为结构搜索提供提示）"
    };
    private readonly ToggleSwitch _reusePreviousAlignmentToggle = new()
    {
        Header = "优先复用上次对齐结果",
        OffContent = "关闭（每次从全图开始搜索）",
        OnContent = "开启（局部失败后自动回退全图）"
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
    private bool _refreshing;
    private bool _subscribedToRuntime;
    private bool _viewBuilt;

    public MapStatusPage()
    {
        try
        {
            BuildView();
            _viewBuilt = true;
        }
        catch (Exception exception)
        {
            ReportPageFailure("build", exception);
            Content = CreatePageFailureView(exception);
        }
        Loaded += MapStatusPage_Loaded;
        Unloaded += MapStatusPage_Unloaded;
    }

    private void MapStatusPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_subscribedToRuntime)
            {
                _runtime.StateChanged += Runtime_StateChanged;
                _subscribedToRuntime = true;
            }
            if (_viewBuilt)
                TryRefresh("loaded");
        }
        catch (Exception exception)
        {
            ReportPageFailure("loaded", exception);
        }
    }

    private void MapStatusPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribedToRuntime)
            return;
        _runtime.StateChanged -= Runtime_StateChanged;
        _subscribedToRuntime = false;
    }

    private FrameworkElement CreatePageFailureView(Exception exception) =>
        new StackPanel
        {
            Margin = new Thickness(36, 31, 36, 38),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "状态页部分功能加载失败",
                    FontSize = 29,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                },
                new TextBlock
                {
                    Text = "错误已隔离，地图运行时和应用其他页面不会因此退出。",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = $"{exception.GetType().Name}: {exception.Message}",
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };

    private void ReportPageFailure(string stage, Exception exception)
    {
        System.Diagnostics.Debug.WriteLine(
            $"Map status page {stage} failed: {exception}");
        try
        {
            _runtime.LogCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Error,
                $"Map status page {stage} failed: {exception.Message}",
                details: new()
                {
                    ["stage"] = stage,
                    ["exceptionType"] = exception.GetType().FullName ?? exception.GetType().Name
                });
        }
        catch (Exception loggingException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Map status page failure could not be logged: {loggingException}");
        }
    }

    private void BuildView()
    {
        _root = new Grid
        {
            Margin = new Thickness(36, 31, 36, 38),
            MinHeight = 700,
            IsTabStop = true
        };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.KeyDown += Root_KeyDown;
        _root.PointerPressed += Root_PointerPressed;

        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = "状态",
            FontSize = 29,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "管理自动扫描、游戏内手动识别、覆盖图层与识别参数。",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        _root.Children.Add(header);

        var content = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(0, 26, 0, 0),
            Width = 720,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        _enabledToggle.Toggled += EnabledToggle_Toggled;
        content.Children.Add(_enabledToggle);
        _firstScanStrategyToggle.Toggled += FirstScanStrategy_Toggled;
        content.Children.Add(_firstScanStrategyToggle);
        content.Children.Add(CreateBindingRow(
            "游戏地图开关",
            "绑定游戏本身的地图键或鼠标键；打开时只刷新已选地图的对齐，关闭时仅隐藏。",
            _gameMapBinding,
            MapRuntimeBindingTarget.GameMapToggle));
        content.Children.Add(CreateBindingRow(
            "外置控件层",
            "在游戏前台呼出独立的对局状态和玩家序号控件；再次按键关闭。",
            _controlPanelBinding,
            MapRuntimeBindingTarget.ControlPanelToggle));
        content.Children.Add(CreateBindingRow(
            "快捷扫描",
            "冻结当前 dwrg.exe 地图区域，从地图库中选择地图并完成首次双门对齐。",
            _quickBinding,
            MapRuntimeBindingTarget.QuickScan));
        content.Children.Add(CreateBindingRow(
            "识别图层",
            "即使没有地图结果，也可以显示或隐藏左上角状态。",
            _overlayBinding,
            MapRuntimeBindingTarget.OverlayToggle));
        content.Children.Add(CreateBindingRow(
            "手动识别",
            "在冻结的游戏画面中依次框选大门和侧门。",
            _manualBinding,
            MapRuntimeBindingTarget.ManualRecognition));
        content.Children.Add(CreateBindingRow(
            "切换楼层",
            "手动在 1F 与 2F 之间切换小地图显示的楼层。仅在已识别地图后生效。",
            _switchFloorBinding,
            MapRuntimeBindingTarget.SwitchFloor));

        _collectLogsToggle.Toggled += CollectLogsToggle_Toggled;
        content.Children.Add(_collectLogsToggle);
        _collectResearchToggle.Toggled += CollectResearchToggle_Toggled;
        content.Children.Add(_collectResearchToggle);

        var displayPanel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left };

        // --- 常规 ---
        displayPanel.Children.Add(CreateCategoryHeader("常规"));
        _overlayStatusToggle.Toggled += OverlayStatusToggle_Toggled;
        displayPanel.Children.Add(_overlayStatusToggle);
        _reverseAlternateDisplayToggle.Toggled += ReverseAlternateDisplay_Toggled;
        displayPanel.Children.Add(_reverseAlternateDisplayToggle);
        _allowExtendToggle.Toggled += AllowExtendToggle_Toggled;
        displayPanel.Children.Add(_allowExtendToggle);
        _miniMapEnabledToggle.Toggled += MiniMapEnabledToggle_Toggled;
        displayPanel.Children.Add(_miniMapEnabledToggle);
        _playerTrackingToggle.Toggled += PlayerTracking_Toggled;
        displayPanel.Children.Add(_playerTrackingToggle);
        _forceBestResultToggle.Toggled += RecognitionTuning_Changed;
        _forceCandidateToggle.Toggled += RecognitionTuning_Changed;
        _alignmentMode.SelectionChanged += AlignmentMode_SelectionChanged;
        displayPanel.Children.Add(_alignmentMode);
        _statusOpacityBox.ValueChanged += StatusOpacity_Changed;
        displayPanel.Children.Add(_statusOpacityBox);
        _statusOffsetXBox.ValueChanged += StatusOffsetX_Changed;
        displayPanel.Children.Add(_statusOffsetXBox);
        _statusOffsetYBox.ValueChanged += StatusOffsetY_Changed;
        displayPanel.Children.Add(_statusOffsetYBox);

        // --- 大地图 ---
        displayPanel.Children.Add(CreateCategoryHeader("大地图"));
        _showGateMarkersToggle.Toggled += ShowGateMarkers_Toggled;
        displayPanel.Children.Add(_showGateMarkersToggle);
        _showAuxiliaryAnchorsToggle.Toggled += ShowAuxiliaryAnchors_Toggled;
        displayPanel.Children.Add(_showAuxiliaryAnchorsToggle);
        _showTextAnnotationsToggle.Toggled += ShowTextAnnotations_Toggled;
        displayPanel.Children.Add(_showTextAnnotationsToggle);
        _showBoxAnnotationsToggle.Toggled += ShowBoxAnnotations_Toggled;
        displayPanel.Children.Add(_showBoxAnnotationsToggle);

        // --- 小地图 ---
        displayPanel.Children.Add(CreateCategoryHeader("小地图"));
        _showGateMarkersOnMiniMapToggle.Toggled += ShowGateMarkersOnMiniMap_Toggled;
        displayPanel.Children.Add(_showGateMarkersOnMiniMapToggle);
        _showAuxiliaryAnchorsOnMiniMapToggle.Toggled += ShowAuxiliaryAnchorsOnMiniMap_Toggled;
        displayPanel.Children.Add(_showAuxiliaryAnchorsOnMiniMapToggle);
        _showTextAnnotationsOnMiniMapToggle.Toggled += ShowTextAnnotationsOnMiniMap_Toggled;
        displayPanel.Children.Add(_showTextAnnotationsOnMiniMapToggle);
        _showBoxAnnotationsOnMiniMapToggle.Toggled += ShowBoxAnnotationsOnMiniMap_Toggled;
        displayPanel.Children.Add(_showBoxAnnotationsOnMiniMapToggle);
        _showFloorOnMiniMapToggle.Toggled += ShowFloorOnMiniMap_Toggled;
        displayPanel.Children.Add(_showFloorOnMiniMapToggle);
        _miniMapScaleBox.ValueChanged += MiniMapScaleBox_Changed;
        displayPanel.Children.Add(_miniMapScaleBox);
        _miniMapOpacityBox.ValueChanged += MiniMapOpacity_Changed;
        displayPanel.Children.Add(_miniMapOpacityBox);
        _miniMapOffsetXBox.ValueChanged += MiniMapOffsetX_Changed;
        displayPanel.Children.Add(_miniMapOffsetXBox);
        _miniMapOffsetYBox.ValueChanged += MiniMapOffsetY_Changed;
        displayPanel.Children.Add(_miniMapOffsetYBox);

        content.Children.Add(new Expander
        {
            Header = "显示与渲染",
            Content = displayPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });

        var calibrationButton = new Button
        {
            Content = "校准地图区域",
            MinWidth = 140,
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8)
        };
        calibrationButton.Click += async (_, _) => await CalibrateViewportAsync();
        content.Children.Add(calibrationButton);

        var floorCalibrationButton = new Button
        {
            Content = "校准楼层显示区",
            MinWidth = 160,
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8)
        };
        floorCalibrationButton.Click += async (_, _) =>
            await CalibrateFloorDisplayAsync();
        content.Children.Add(floorCalibrationButton);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        _scanButton.Click += async (_, _) => await RunDelayedAsync(
            _scanButton,
            "3 秒后扫描",
            _runtime.RunQuickScanAsync);
        _manualButton.Click += async (_, _) => await RunDelayedAsync(
            _manualButton,
            "3 秒后手动识别",
            _runtime.RunManualRecognitionAsync);
        actions.Children.Add(_scanButton);
        actions.Children.Add(_manualButton);
        var openLogsButton = new Button
        {
            Content = "打开日志文件夹",
            MinWidth = 140,
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 0)
        };
        openLogsButton.Click += (_, _) =>
        {
            var logsPath = _runtime.LogCollector.LogDirectory;
            System.IO.Directory.CreateDirectory(logsPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = logsPath,
                UseShellExecute = true
            });
        };
        actions.Children.Add(openLogsButton);
        var openResearchButton = new Button
        {
            Content = "打开研究数据文件夹",
            MinWidth = 170,
            MinHeight = 42,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 0)
        };
        openResearchButton.Click += (_, _) =>
        {
            var researchPath = _runtime.ResearchCollector.RootDirectory;
            System.IO.Directory.CreateDirectory(researchPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = researchPath,
                UseShellExecute = true
            });
        };
        actions.Children.Add(openResearchButton);
        content.Children.Add(actions);

        content.Children.Add(new TextBlock
        {
            Text = "识别参数",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        });
        var basicParameters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14
        };
        basicParameters.Children.Add(_gateThreshold);
        basicParameters.Children.Add(_minimumConfidence);
        content.Children.Add(basicParameters);

        var advancedPanel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Left };
        advancedPanel.Children.Add(CreateCategoryHeader("常用识别"));
        advancedPanel.Children.Add(_highConfidenceThreshold);
        _sideEntranceFeatureRadius.ValueChanged += SideEntranceFeatureRadius_Changed;
        advancedPanel.Children.Add(_sideEntranceFeatureRadius);
        _skipFloorRecognitionToggle.Toggled += SkipFloorRecognition_Toggled;
        advancedPanel.Children.Add(CreateCategoryHeader("稳定性与时序"));
        advancedPanel.Children.Add(_skipFloorRecognitionToggle);
        _skipStabilityConfirmationToggle.Toggled += SkipStabilityConfirmation_Toggled;
        advancedPanel.Children.Add(_skipStabilityConfirmationToggle);
        _mediumConfidenceThreshold.ValueChanged += SessionTuning_ValueChanged;
        advancedPanel.Children.Add(_mediumConfidenceThreshold);
        advancedPanel.Children.Add(_openingAnimationDelay);
        advancedPanel.Children.Add(_openingTimeout);
        advancedPanel.Children.Add(_stableFrameCount);
        advancedPanel.Children.Add(_stableFrameInterval);
        advancedPanel.Children.Add(_stableFrameDifference);
        advancedPanel.Children.Add(_mediumConfidenceFrames);
        advancedPanel.Children.Add(_candidateStabilityPixels);
        advancedPanel.Children.Add(_nativeScaleChangeRatio);
        advancedPanel.Children.Add(CreateCategoryHeader("结构配准"));
        advancedPanel.Children.Add(_vectorTolerance);
        advancedPanel.Children.Add(_ambiguityMargin);
        advancedPanel.Children.Add(_confirmationAdvantage);
        _miniMapScale.ValueChanged += MiniMapScale_Changed;
        advancedPanel.Children.Add(_miniMapScale);
        advancedPanel.Children.Add(new TextBlock
        {
            Text = "无门结构配准",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        });
        advancedPanel.Children.Add(_auxiliaryAnchorToggle);
        advancedPanel.Children.Add(_auxiliaryMaxTemplates);
        advancedPanel.Children.Add(_auxiliaryDirectLockConfidence);
        advancedPanel.Children.Add(_viewportEdgeMargin);
        advancedPanel.Children.Add(_reusePreviousAlignmentToggle);
        advancedPanel.Children.Add(_previousAlignmentSearchRadius);
        advancedPanel.Children.Add(_structureEccToggle);
        advancedPanel.Children.Add(new TextBlock
        {
            Text = "AKAZE 特征匹配",
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0)
        });
        advancedPanel.Children.Add(_featureVotingToggle);
        advancedPanel.Children.Add(_featureRatioThreshold);
        advancedPanel.Children.Add(_featureInlierTolerance);
        advancedPanel.Children.Add(_featureMaxCandidates);
        advancedPanel.Children.Add(_structureChamfer);
        advancedPanel.Children.Add(_structureCoverage);
        advancedPanel.Children.Add(_structureMargin);
        advancedPanel.Children.Add(_structureOccupancy);
        advancedPanel.Children.Add(_structurePartitions);
        advancedPanel.Children.Add(_structureEdgeTolerance);
        advancedPanel.Children.Add(_structureTopCandidates);
        advancedPanel.Children.Add(new Expander
        {
            Header = "性能与搜索（调试参数）",
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _warmGateSearchBudget,
                    _confirmationGateSearchBudget,
                    _confirmationRoiPaddingFactor,
                    _confirmationRoiMinimumPadding,
                    _confirmationMaximumMapDrag,
                    _structureBudget,
                    _fastCoarseDownsample,
                    _fastCoarseTopK
                }
            }
        });
        advancedPanel.Children.Add(new Expander
        {
            Header = "楼层识别",
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _floorMinimumConfidence,
                    _floorLocalizationConfidence,
                    _floorRecognitionTimeout,
                    _floorFirstConfirmationFrames,
                    _floorSecondConfirmationFrames
                }
            }
        });
        advancedPanel.Children.Add(new Expander
        {
            Header = "玩家跟踪",
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _playerMinimumConfidence,
                    _playerFailureLimit,
                    _playerStaleHideMilliseconds
                }
            }
        });
        advancedPanel.Children.Add(new Expander
        {
            Header = "实验功能（可能影响稳定性）",
            IsExpanded = false,
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    _forceBestResultToggle,
                    _forceCandidateToggle,
                    _visibleAwareToggle,
                    _fastAlignmentToggle,
                    _fastShadowToggle,
                    _structureDebugToggle
                }
            }
        });
        var restoreButton = new Button
        {
            Content = "恢复默认值",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        restoreButton.Click += async (_, _) =>
        {
            try
            {
                await _runtime.RestoreRecognitionTuningDefaultsAsync();
                await _runtime.RestoreStructureRegistrationTuningDefaultsAsync();
                await _runtime.RestoreSessionTuningDefaultsAsync();
                await _runtime.RestoreFloorRecognitionTuningDefaultsAsync();
                await _runtime.RestorePlayerTrackingTuningDefaultsAsync();
            }
            catch (Exception exception)
            {
                _status.Text = exception.Message;
            }
        };
        advancedPanel.Children.Add(restoreButton);
        content.Children.Add(new Expander
        {
            Header = "高级参数",
            Content = advancedPanel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });

        _gateThreshold.ValueChanged += Tuning_ValueChanged;
        _minimumConfidence.ValueChanged += Tuning_ValueChanged;
        _vectorTolerance.ValueChanged += Tuning_ValueChanged;
        _ambiguityMargin.ValueChanged += Tuning_ValueChanged;
        _confirmationAdvantage.ValueChanged += Tuning_ValueChanged;
        _highConfidenceThreshold.ValueChanged += SessionTuning_ValueChanged;
        _openingAnimationDelay.ValueChanged += SessionTuning_ValueChanged;
        _openingTimeout.ValueChanged += SessionTuning_ValueChanged;
        _stableFrameCount.ValueChanged += SessionTuning_ValueChanged;
        _stableFrameInterval.ValueChanged += SessionTuning_ValueChanged;
        _stableFrameDifference.ValueChanged += SessionTuning_ValueChanged;
        _mediumConfidenceFrames.ValueChanged += SessionTuning_ValueChanged;
        _candidateStabilityPixels.ValueChanged += SessionTuning_ValueChanged;
        _nativeScaleChangeRatio.ValueChanged += SessionTuning_ValueChanged;
        _warmGateSearchBudget.ValueChanged += Tuning_ValueChanged;
        _confirmationGateSearchBudget.ValueChanged += Tuning_ValueChanged;
        _confirmationRoiPaddingFactor.ValueChanged += Tuning_ValueChanged;
        _confirmationRoiMinimumPadding.ValueChanged += Tuning_ValueChanged;
        _confirmationMaximumMapDrag.ValueChanged += Tuning_ValueChanged;
        _floorMinimumConfidence.ValueChanged += FloorTuning_ValueChanged;
        _floorLocalizationConfidence.ValueChanged += FloorTuning_ValueChanged;
        _floorRecognitionTimeout.ValueChanged += FloorTuning_ValueChanged;
        _floorFirstConfirmationFrames.ValueChanged += FloorTuning_ValueChanged;
        _floorSecondConfirmationFrames.ValueChanged += FloorTuning_ValueChanged;
        _playerMinimumConfidence.ValueChanged += PlayerTuning_ValueChanged;
        _playerFailureLimit.ValueChanged += PlayerTuning_ValueChanged;
        _playerStaleHideMilliseconds.ValueChanged += PlayerTuning_ValueChanged;
        _auxiliaryAnchorToggle.Toggled += StructureTuning_Changed;
        _reusePreviousAlignmentToggle.Toggled += StructureTuning_Changed;
        _previousAlignmentSearchRadius.ValueChanged +=
            StructureTuning_ValueChanged;
        _structureEccToggle.Toggled += StructureTuning_Changed;
        _structureDebugToggle.Toggled += StructureTuning_Changed;
        _structureChamfer.ValueChanged += StructureTuning_ValueChanged;
        _structureCoverage.ValueChanged += StructureTuning_ValueChanged;
        _structureMargin.ValueChanged += StructureTuning_ValueChanged;
        _auxiliaryMaxTemplates.ValueChanged += StructureTuning_ValueChanged;
        _auxiliaryDirectLockConfidence.ValueChanged += StructureTuning_ValueChanged;
        _viewportEdgeMargin.ValueChanged += StructureTuning_ValueChanged;
        _featureVotingToggle.Toggled += StructureTuning_Changed;
        _fastAlignmentToggle.Toggled += StructureTuning_Changed;
        _visibleAwareToggle.Toggled += StructureTuning_Changed;
        _fastShadowToggle.Toggled += StructureTuning_Changed;
        _featureRatioThreshold.ValueChanged += StructureTuning_ValueChanged;
        _featureInlierTolerance.ValueChanged += StructureTuning_ValueChanged;
        _featureMaxCandidates.ValueChanged += StructureTuning_ValueChanged;
        _structureOccupancy.ValueChanged += StructureTuning_ValueChanged;
        _structurePartitions.ValueChanged += StructureTuning_ValueChanged;
        _structureEdgeTolerance.ValueChanged += StructureTuning_ValueChanged;
        _structureTopCandidates.ValueChanged += StructureTuning_ValueChanged;
        _structureBudget.ValueChanged += StructureTuning_ValueChanged;
        _elevationButton.Click += ElevationButton_Click;
        content.Children.Add(_elevationButton);

        Grid.SetRow(content, 1);
        _root.Children.Add(content);

        var diagnostics = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(0, 30, 0, 0),
            Width = 720,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        diagnostics.Children.Add(CreateDiagnostic("图层", _overlayState));
        diagnostics.Children.Add(CreateDiagnostic("游戏权限", _permissionState));
        diagnostics.Children.Add(CreateDiagnostic("地图区域", _calibrationState));
        diagnostics.Children.Add(CreateDiagnostic("楼层显示区", _floorCalibrationState));
        diagnostics.Children.Add(CreateDiagnostic("玩家序号资源", _playerCalibrationState));
        diagnostics.Children.Add(CreateDiagnostic("当前楼层", _floorState));
        diagnostics.Children.Add(CreateDiagnostic("地图数据", _mapReadiness));
        diagnostics.Children.Add(CreateDiagnostic("当前选择", _selectedMapState));
        diagnostics.Children.Add(CreateDiagnostic("当前对齐", _alignmentState));
        diagnostics.Children.Add(CreateDiagnostic("开图会话", _sessionState));
        diagnostics.Children.Add(CreateDiagnostic("对局状态", _matchState));
        diagnostics.Children.Add(CreateDiagnostic("外置控件层", _controlPanelState));
        diagnostics.Children.Add(CreateDiagnostic("玩家坐标", _playerState));
        diagnostics.Children.Add(CreateDiagnostic("最近识别", _lastResult));
        diagnostics.Children.Add(CreateDiagnostic("阶段耗时", _timings));
        diagnostics.Children.Add(CreateDiagnostic("日志收集", _logState));
        diagnostics.Children.Add(CreateDiagnostic("运行状态", _status));
        diagnostics.Children.Add(CreateDiagnostic("研究数据采集", _researchState));
        Grid.SetRow(diagnostics, 2);
        _root.Children.Add(diagnostics);

        Content = new ScrollViewer
        {
            Content = _root,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private UIElement CreateBindingRow(
        string title,
        string description,
        TextBlock value,
        MapRuntimeBindingTarget target)
    {
        var panel = new Grid { ColumnSpacing = 18 };
        panel.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star)
        });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(value);
        panel.Children.Add(text);
        var button = new Button
        {
            Content = "设置按键",
            MinWidth = 98,
            MinHeight = 38,
            Background = new SolidColorBrush(Color.FromArgb(255, 242, 242, 242)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 218, 218, 218)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        button.Click += (_, _) => BeginRecording(target);
        Grid.SetColumn(button, 1);
        panel.Children.Add(button);
        return panel;
    }

    private static UIElement CreateDiagnostic(string title, TextBlock value)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        panel.Children.Add(value);
        return panel;
    }

    private static TextBlock CreateMutedText() => new()
    {
        FontSize = 13,
        Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96)),
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock CreateCategoryHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0)
    };

    private static Button CreateActionButton(string text) => new()
    {
        Content = text,
        Background = new SolidColorBrush(Color.FromArgb(255, 46, 132, 225)),
        Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
        MinWidth = 150,
        MinHeight = 45,
        HorizontalAlignment = HorizontalAlignment.Left,
        CornerRadius = new CornerRadius(8)
    };

    private static NumberBox CreatePercentageBox(
        string header,
        double minimum,
        double maximum) =>
        new()
        {
            Header = header,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 220
        };

    private static NumberBox CreateDecimalBox(
        string header,
        double minimum,
        double maximum,
        double step) =>
        new()
        {
            Header = header,
            Minimum = minimum,
            Maximum = maximum,
            SmallChange = step,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 260
        };

    private void BeginRecording(MapRuntimeBindingTarget target)
    {
        _recording = target;
        _status.Text = target switch
        {
            MapRuntimeBindingTarget.GameMapToggle => "请按下游戏中用于打开/关闭地图的键盘或鼠标按键。",
            MapRuntimeBindingTarget.ControlPanelToggle => "请按下用于开启/关闭外置控件层的键盘或鼠标按键。",
            MapRuntimeBindingTarget.QuickScan => "请按下用于快捷扫描的键盘或鼠标按键。",
            MapRuntimeBindingTarget.OverlayToggle => "请按下用于切换识别图层的键盘或鼠标按键。",
            MapRuntimeBindingTarget.SwitchFloor => "请按下用于切换小地图楼层的键盘或鼠标按键。",
            _ => "请按下用于手动识别的键盘或鼠标按键。"
        };
        _root?.Focus(FocusState.Programmatic);
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recording is null)
            return;
        e.Handled = true;
        await SaveRecordedBindingAsync(new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = (uint)e.Key
        });
    }

    private async void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_recording is null)
            return;
        var properties = e.GetCurrentPoint(_root).Properties;
        var button = properties.IsLeftButtonPressed
            ? MapMouseButton.Left
            : properties.IsRightButtonPressed
                ? MapMouseButton.Right
                : properties.IsMiddleButtonPressed
                    ? MapMouseButton.Middle
                    : properties.IsXButton1Pressed
                        ? MapMouseButton.XButton1
                        : MapMouseButton.XButton2;
        e.Handled = true;
        await SaveRecordedBindingAsync(new MapInputBinding
        {
            Kind = MapInputBindingKind.Mouse,
            MouseButton = button
        });
    }

    private async Task SaveRecordedBindingAsync(MapInputBinding binding)
    {
        if (_recording is not { } target)
            return;
        _recording = null;
        try
        {
            await _runtime.SetBindingAsync(target, binding);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        Refresh();
    }

    private async Task CalibrateViewportAsync()
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "准备校准地图区域",
            Content =
                "点击开始后，请在 3 秒内切换到第五人格并打开完整地图。"
                + "随后框选整张地图画布的外边缘，不要只框建筑主体或两个门。"
                + "程序只保存相对坐标，截图不会写入磁盘。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            return;

        _status.Text = "请切换到游戏，3 秒后捕获完整地图……";
        await Task.Delay(3000);
        if (!_runtime.TryCaptureCalibrationFrame(out var frame, out var failureReason)
            || frame is null)
        {
            _status.Text = failureReason;
            return;
        }

        using (frame)
        {
            ((App)Application.Current).MainWindow.Activate();
            var region = await MapViewportCalibrationDialog.ShowAsync(
                XamlRoot,
                frame,
                _runtime.Settings.MapViewportRegion,
                "校准游戏地图区域",
                "请沿完整地图画布的外边缘框选，不要只框建筑主体、两个门或它们之间的区域。只保存相对坐标，截图不会写入磁盘。");
            if (region is null)
                return;
            await _runtime.SetMapViewportAsync(
                region,
                (int)Math.Round(frame.ClientBounds.Width),
                (int)Math.Round(frame.ClientBounds.Height));
        }
        Refresh();
    }

    private async Task CalibrateFloorDisplayAsync()
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "准备校准楼层显示区",
            Content =
                "点击开始后，请在 3 秒内切换到第五人格并打开完整地图。"
                + "随后完整框选包含 1 和 2 两个按钮的楼层显示区域。"
                + "程序只保存相对坐标，截图不会写入磁盘。",
            PrimaryButtonText = "开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            return;

        _status.Text = "请切换到游戏，3 秒后捕获楼层显示区……";
        await Task.Delay(3000);
        if (!_runtime.TryCaptureCalibrationFrame(out var frame, out var failureReason)
            || frame is null)
        {
            _status.Text = failureReason;
            return;
        }

        using (frame)
        {
            ((App)Application.Current).MainWindow.Activate();
            var region = await MapViewportCalibrationDialog.ShowAsync(
                XamlRoot,
                frame,
                _runtime.Settings.FloorDisplayRegion,
                "校准楼层显示区",
                "请完整框选 1F/2F 双按钮区域，保留两个按钮及其高亮背景；不要只框单个数字。只保存相对坐标，截图不会写入磁盘。");
            if (region is null)
                return;
            try
            {
                await _runtime.SetFloorDisplayRegionAsync(
                    region,
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height),
                    frame);
            }
            catch (InvalidOperationException exception)
            {
                _status.Text = exception.Message;
                return;
            }
        }
        Refresh();
    }

    private async Task RunDelayedAsync(
        Button button,
        string idleText,
        Func<Task> action)
    {
        button.IsEnabled = false;
        button.Content = "请切换到游戏……";
        await Task.Delay(3000);
        await action();
        button.Content = idleText;
        button.IsEnabled = true;
    }

    private async void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetEnabledAsync(_enabledToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void OverlayStatusToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetOverlayStatusVisibleAsync(_overlayStatusToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void ReverseAlternateDisplay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetReverseAlternateDisplayAsync(
                _reverseAlternateDisplayToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void ShowGateMarkers_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowGateMarkersAsync(_showGateMarkersToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowAuxiliaryAnchors_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowAuxiliaryAnchorsAsync(_showAuxiliaryAnchorsToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowTextAnnotations_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowTextAnnotationsAsync(_showTextAnnotationsToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowBoxAnnotations_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowBoxAnnotationsAsync(_showBoxAnnotationsToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowGateMarkersOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowGateMarkersOnMiniMapAsync(_showGateMarkersOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowAuxiliaryAnchorsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowAuxiliaryAnchorsOnMiniMapAsync(_showAuxiliaryAnchorsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowTextAnnotationsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowTextAnnotationsOnMiniMapAsync(_showTextAnnotationsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowBoxAnnotationsOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowBoxAnnotationsOnMiniMapAsync(_showBoxAnnotationsOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void ShowFloorOnMiniMap_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        try { await _runtime.SetShowFloorOnMiniMapAsync(_showFloorOnMiniMapToggle.IsOn); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapScaleBox_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapScaleAsync(args.NewValue / 100d); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapOpacity_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapOpacityAsync(args.NewValue / 100d); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapOffsetX_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapOffsetXAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void MiniMapOffsetY_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetMiniMapOffsetYAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void StatusOpacity_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetStatusOpacityAsync(args.NewValue / 100d); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void StatusOffsetX_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetStatusOffsetXAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void StatusOffsetY_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue)) return;
        try { await _runtime.SetStatusOffsetYAsync(args.NewValue); }
        catch (Exception exception) { _status.Text = exception.Message; Refresh(); }
    }

    private async void CollectLogsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetCollectLogsAsync(_collectLogsToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void CollectResearchToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetCollectAlignmentResearchDataAsync(
                _collectResearchToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void FirstScanStrategy_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            var strategy = _firstScanStrategyToggle.IsOn
                ? FirstScanStrategy.SideEntrance
                : FirstScanStrategy.DoubleGate;
            await _runtime.SetFirstScanStrategyAsync(strategy);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        Refresh();
    }

    private async void SideEntranceFeatureRadius_Changed(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue))
            return;
        var newRadius = (int)Math.Round(args.NewValue);
        if (newRadius == _runtime.Settings.RecognitionTuning.SideEntranceFeatureRadius)
            return;
        try
        {
            // 先保存新半径
            var tuning = _runtime.Settings.RecognitionTuning.Clone();
            tuning.SideEntranceFeatureRadius = newRadius;
            await _runtime.SetRecognitionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            return;
        }

        // 弹出进度对话框，批量重建特征图
        await ShowSideEntranceFeatureRebuildDialogAsync();
    }

    private async Task ShowSideEntranceFeatureRebuildDialogAsync()
    {
        var progressBar = new ProgressBar
        {
            IsIndeterminate = false,
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            MinWidth = 320
        };
        var progressText = new TextBlock
        {
            Text = "准备中……",
            Margin = new Thickness(0, 6, 0, 0)
        };
        var content = new StackPanel
        {
            Spacing = 0,
            Children =
            {
                new TextBlock
                {
                    Text = "即将对所有地图重新生成侧门特征素材，处理时间取决于地图数量。",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 12)
                },
                progressBar,
                progressText
            }
        };
        var dialog = new ContentDialog
        {
            Title = "重新预处理侧门特征",
            Content = content,
            XamlRoot = XamlRoot
        };

        var cts = new CancellationTokenSource();
        var progress = new Progress<(int done, int total)>(report =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var (done, total) = report;
                if (total > 0)
                {
                    progressBar.Maximum = total;
                    progressBar.Value   = done;
                    progressText.Text   = $"已处理 {done} / {total} 张";
                }
            });
        });

        // 异步启动重建，完成后关闭对话框
        _ = Task.Run(async () =>
        {
            try
            {
                await _runtime.RebuildSideEntranceFeaturesAsync(progress, cts.Token);
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() => dialog.Hide());
            }
        });

        await dialog.ShowAsync();
    }

    private async void SkipFloorRecognition_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try        {
            await _runtime.SetSkipFloorRecognitionAsync(
                _skipFloorRecognitionToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void SkipStabilityConfirmation_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetSkipStabilityConfirmationAsync(
                _skipStabilityConfirmationToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AllowExtendToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetAllowMapExtendBeyondBoundsAsync(_allowExtendToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void MiniMapEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetPersistentMiniMapEnabledAsync(_miniMapEnabledToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void PlayerTracking_Toggled(object sender, RoutedEventArgs e)
    {
        if (_refreshing)
            return;
        try
        {
            await _runtime.SetPlayerTrackingEnabledAsync(_playerTrackingToggle.IsOn);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void MiniMapScale_Changed(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing)
            return;
        if (double.IsNaN(args.NewValue))
            return;
        try
        {
            await _runtime.SetMiniMapScaleAsync(args.NewValue / 100d);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void AlignmentMode_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_refreshing
            || _alignmentMode.SelectedItem is not AlignmentModeChoice choice)
        {
            return;
        }
        try
        {
            await _runtime.SetOverlayAlignmentModeAsync(choice.Mode);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void Tuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveRecognitionTuningAsync();

    private async void SessionTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_refreshing || double.IsNaN(args.NewValue))
            return;
        try
        {
            await SaveSessionTuningAsync();
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void FloorTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveFloorTuningAsync();

    private async void PlayerTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SavePlayerTuningAsync();

    private async void RecognitionTuning_Changed(
        object sender,
        RoutedEventArgs e) =>
        await SaveRecognitionTuningAsync();

    private async Task SaveRecognitionTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.RecognitionTuning.Clone();
            tuning.GateTemplateThreshold = _gateThreshold.Value / 100d;
            tuning.MinimumConfidence = _minimumConfidence.Value / 100d;
            tuning.VectorErrorTolerance = _vectorTolerance.Value;
            tuning.AmbiguityMargin = _ambiguityMargin.Value;
            tuning.ConfirmationAdvantage = _confirmationAdvantage.Value;
            tuning.ForceBestRecognitionResult = _forceBestResultToggle.IsOn;
            tuning.ForceCandidateSelection = _forceCandidateToggle.IsOn;
            tuning.WarmGateSearchBudgetMs = (int)Math.Round(_warmGateSearchBudget.Value);
            tuning.ConfirmationGateSearchBudgetMs =
                (int)Math.Round(_confirmationGateSearchBudget.Value);
            tuning.ConfirmationRoiTemplatePaddingFactor =
                _confirmationRoiPaddingFactor.Value;
            tuning.ConfirmationRoiMinimumPaddingPixels =
                (int)Math.Round(_confirmationRoiMinimumPadding.Value);
            tuning.ConfirmationMaximumMapDragPixelsPerSecond =
                _confirmationMaximumMapDrag.Value;
            await _runtime.SetRecognitionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SaveSessionTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.SessionTuning.Clone();
            tuning.HighConfidence = _highConfidenceThreshold.Value / 100d;
            tuning.MediumConfidence = _mediumConfidenceThreshold.Value / 100d;
            tuning.OpeningAnimationDelayMilliseconds =
                (int)Math.Round(_openingAnimationDelay.Value);
            tuning.OpeningTimeoutMilliseconds =
                (int)Math.Round(_openingTimeout.Value);
            tuning.StableFrameCount = (int)Math.Round(_stableFrameCount.Value);
            tuning.StableFrameIntervalMilliseconds =
                (int)Math.Round(_stableFrameInterval.Value);
            tuning.StableFrameDifference = _stableFrameDifference.Value;
            tuning.MediumConfidenceFrames =
                (int)Math.Round(_mediumConfidenceFrames.Value);
            tuning.CandidateStabilityPixels = _candidateStabilityPixels.Value;
            tuning.NativeScaleChangeRatio = _nativeScaleChangeRatio.Value / 100d;
            await _runtime.SetSessionTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SaveFloorTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            await _runtime.SetFloorRecognitionTuningAsync(new MapFloorRecognitionTuning
            {
                MinimumConfidence = _floorMinimumConfidence.Value / 100d,
                MinimumLocalizationConfidence =
                    _floorLocalizationConfidence.Value / 100d,
                MaximumRecognitionWindowMilliseconds =
                    (int)Math.Round(_floorRecognitionTimeout.Value),
                FirstFloorConfirmationFrames =
                    (int)Math.Round(_floorFirstConfirmationFrames.Value),
                SecondFloorConfirmationFrames =
                    (int)Math.Round(_floorSecondConfirmationFrames.Value)
            });
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async Task SavePlayerTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            await _runtime.SetPlayerTrackingTuningAsync(new MapPlayerTrackingTuning
            {
                MinimumConfidence = _playerMinimumConfidence.Value / 100d,
                LocalSearchFailureLimit = (int)Math.Round(_playerFailureLimit.Value),
                StaleHideMilliseconds =
                    (int)Math.Round(_playerStaleHideMilliseconds.Value)
            });
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void StructureTuning_Changed(object sender, RoutedEventArgs e) =>
        await SaveStructureTuningAsync();

    private async void StructureTuning_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        await SaveStructureTuningAsync();

    private async Task SaveStructureTuningAsync()
    {
        if (_refreshing || _runtime.IsScanning)
            return;
        try
        {
            var tuning = _runtime.Settings.StructureRegistrationTuning.Clone();
            tuning.UseAuxiliaryAnchorRecognition = _auxiliaryAnchorToggle.IsOn;
            tuning.ReusePreviousAlignmentResult =
                _reusePreviousAlignmentToggle.IsOn;
            if (double.IsFinite(_previousAlignmentSearchRadius.Value))
            {
                tuning.PreviousAlignmentSearchRadiusPixels =
                    (int)Math.Round(_previousAlignmentSearchRadius.Value);
            }
            tuning.EnableEccRefinement = _structureEccToggle.IsOn;
            tuning.EnableDebugOutput = _structureDebugToggle.IsOn;
            tuning.MaximumChamferPixels = _structureChamfer.Value;
            tuning.MinimumEdgeCoverage = _structureCoverage.Value / 100d;
            tuning.MinimumCandidateMargin = _structureMargin.Value / 100d;
            if (double.IsFinite(_auxiliaryMaxTemplates.Value))
            {
                tuning.MaximumAuxiliaryTemplates =
                    (int)Math.Round(_auxiliaryMaxTemplates.Value);
            }
            if (double.IsFinite(_auxiliaryDirectLockConfidence.Value))
                tuning.AuxiliaryDirectLockConfidence =
                    _auxiliaryDirectLockConfidence.Value / 100d;
            if (double.IsFinite(_viewportEdgeMargin.Value))
                tuning.MapViewportEdgeMargin =
                    _viewportEdgeMargin.Value / 100d;
            tuning.EnableFeatureVoting = _featureVotingToggle.IsOn;
            tuning.EnableVisibleMask = _visibleAwareToggle.IsOn;
            tuning.EnableVisibleAwareInjection = _visibleAwareToggle.IsOn;
            tuning.EnableFastAlignment = _fastAlignmentToggle.IsOn;
            tuning.FastAlignmentShadowMode = _fastShadowToggle.IsOn;
            if (double.IsFinite(_featureRatioThreshold.Value))
                tuning.FeatureRatioThreshold = _featureRatioThreshold.Value / 100d;
            if (double.IsFinite(_featureInlierTolerance.Value))
                tuning.FeatureInlierTolerancePixels = _featureInlierTolerance.Value;
            if (double.IsFinite(_featureMaxCandidates.Value))
            {
                tuning.MaximumTranslationCandidates =
                    (int)Math.Round(_featureMaxCandidates.Value);
            }
            if (double.IsFinite(_fastCoarseDownsample.Value))
            {
                tuning.FastCoarseDownsampleFactor =
                    (int)Math.Round(_fastCoarseDownsample.Value);
            }
            if (double.IsFinite(_fastCoarseTopK.Value))
            {
                tuning.FastCoarseTopK = (int)Math.Round(_fastCoarseTopK.Value);
            }
            if (double.IsFinite(_structureOccupancy.Value))
                tuning.MinimumOccupancyCoverage = _structureOccupancy.Value / 100d;
            if (double.IsFinite(_structurePartitions.Value))
            {
                tuning.MinimumConsistentPartitions =
                    (int)Math.Round(_structurePartitions.Value);
            }
            if (double.IsFinite(_structureEdgeTolerance.Value))
                tuning.EdgeDistanceTolerancePixels = _structureEdgeTolerance.Value;
            if (double.IsFinite(_structureTopCandidates.Value))
            {
                tuning.TopCandidateCount =
                    (int)Math.Round(_structureTopCandidates.Value);
            }
            if (double.IsFinite(_structureBudget.Value))
            {
                tuning.StructureFallbackBudgetMilliseconds =
                    (int)Math.Round(_structureBudget.Value);
            }
            await _runtime.SetStructureRegistrationTuningAsync(tuning);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
            Refresh();
        }
    }

    private async void ElevationButton_Click(object sender, RoutedEventArgs e)
    {
        var prompt = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "管理员重启",
            Content = "将请求管理员权限启动新的 Identity Vision Bridge。新进程启动成功后才会关闭当前窗口。",
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await prompt.ShowAsync() != ContentDialogResult.Primary)
            return;
        if (_runtime.TryRestartElevated(out var failureReason))
        {
            ((App)Application.Current).MainWindow.Close();
            return;
        }
        _status.Text = failureReason;
    }

    private void Runtime_StateChanged(object? sender, EventArgs e)
    {
        try
        {
            DispatcherQueue.TryEnqueue(() => TryRefresh("state-changed"));
        }
        catch (Exception exception)
        {
            ReportPageFailure("queue-state-change", exception);
        }
    }

    private void TryRefresh(string stage)
    {
        try
        {
            RefreshCore();
        }
        catch (Exception exception)
        {
            _refreshing = false;
            ReportPageFailure(stage, exception);
            try
            {
                _status.Text =
                    $"状态数据刷新失败，页面其余功能仍可使用：{exception.Message}";
            }
            catch (Exception statusException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Map status page could not show its failure: {statusException}");
            }
        }
    }

    private void Refresh() => TryRefresh("interaction");

    private void RefreshCore()
    {
        _refreshing = true;
        _enabledToggle.IsOn = _runtime.Settings.IsEnabled;
        _overlayStatusToggle.IsOn = _runtime.Settings.ShowOverlayStatus;
        _alignmentMode.SelectedItem = _alignmentMode.Items
            .OfType<AlignmentModeChoice>()
            .FirstOrDefault(choice =>
                choice.Mode == _runtime.Settings.OverlayAlignmentMode);
        var tuning = _runtime.Settings.RecognitionTuning;
        _gateThreshold.Value = tuning.GateTemplateThreshold * 100d;
        _minimumConfidence.Value = tuning.MinimumConfidence * 100d;
        _vectorTolerance.Value = tuning.VectorErrorTolerance;
        _ambiguityMargin.Value = tuning.AmbiguityMargin;
        _confirmationAdvantage.Value = tuning.ConfirmationAdvantage;
        _forceBestResultToggle.IsOn = tuning.ForceBestRecognitionResult;
        _forceCandidateToggle.IsOn = tuning.ForceCandidateSelection;
        _skipFloorRecognitionToggle.IsOn = _runtime.Settings.SkipFloorRecognition;
        var sessionTuning = _runtime.Settings.SessionTuning;
        _skipStabilityConfirmationToggle.IsOn =
            sessionTuning.SkipStabilityConfirmation;
        _highConfidenceThreshold.Value = sessionTuning.HighConfidence * 100d;
        _mediumConfidenceThreshold.Value =
            sessionTuning.MediumConfidence * 100d;
        _openingAnimationDelay.Value = sessionTuning.OpeningAnimationDelayMilliseconds;
        _openingTimeout.Value = sessionTuning.OpeningTimeoutMilliseconds;
        _stableFrameCount.Value = sessionTuning.StableFrameCount;
        _stableFrameInterval.Value = sessionTuning.StableFrameIntervalMilliseconds;
        _stableFrameDifference.Value = sessionTuning.StableFrameDifference;
        _mediumConfidenceFrames.Value = sessionTuning.MediumConfidenceFrames;
        _candidateStabilityPixels.Value = sessionTuning.CandidateStabilityPixels;
        _nativeScaleChangeRatio.Value = sessionTuning.NativeScaleChangeRatio * 100d;
        _warmGateSearchBudget.Value = tuning.WarmGateSearchBudgetMs;
        _confirmationGateSearchBudget.Value = tuning.ConfirmationGateSearchBudgetMs;
        _confirmationRoiPaddingFactor.Value =
            tuning.ConfirmationRoiTemplatePaddingFactor;
        _confirmationRoiMinimumPadding.Value =
            tuning.ConfirmationRoiMinimumPaddingPixels;
        _confirmationMaximumMapDrag.Value =
            tuning.ConfirmationMaximumMapDragPixelsPerSecond;
        var floorTuning = _runtime.Settings.FloorRecognitionTuning;
        _floorMinimumConfidence.Value = floorTuning.MinimumConfidence * 100d;
        _floorLocalizationConfidence.Value =
            floorTuning.MinimumLocalizationConfidence * 100d;
        _floorRecognitionTimeout.Value =
            floorTuning.MaximumRecognitionWindowMilliseconds;
        _floorFirstConfirmationFrames.Value =
            floorTuning.FirstFloorConfirmationFrames;
        _floorSecondConfirmationFrames.Value =
            floorTuning.SecondFloorConfirmationFrames;
        var playerTuning = _runtime.Settings.PlayerTrackingTuning;
        _playerMinimumConfidence.Value = playerTuning.MinimumConfidence * 100d;
        _playerFailureLimit.Value = playerTuning.LocalSearchFailureLimit;
        _playerStaleHideMilliseconds.Value = playerTuning.StaleHideMilliseconds;
        var structureTuning = _runtime.Settings.StructureRegistrationTuning;
        _auxiliaryAnchorToggle.IsOn =
            structureTuning.UseAuxiliaryAnchorRecognition;
        _reusePreviousAlignmentToggle.IsOn =
            structureTuning.ReusePreviousAlignmentResult;
        _previousAlignmentSearchRadius.Value =
            structureTuning.PreviousAlignmentSearchRadiusPixels;
        _structureEccToggle.IsOn = structureTuning.EnableEccRefinement;
        _structureDebugToggle.IsOn = structureTuning.EnableDebugOutput;
        _structureChamfer.Value = structureTuning.MaximumChamferPixels;
        _structureCoverage.Value = structureTuning.MinimumEdgeCoverage * 100d;
        _structureMargin.Value = structureTuning.MinimumCandidateMargin * 100d;
        _auxiliaryMaxTemplates.Value = structureTuning.MaximumAuxiliaryTemplates;
        _auxiliaryDirectLockConfidence.Value =
            structureTuning.AuxiliaryDirectLockConfidence * 100d;
        _viewportEdgeMargin.Value =
            structureTuning.MapViewportEdgeMargin * 100d;
        _featureVotingToggle.IsOn = structureTuning.EnableFeatureVoting;
        _fastAlignmentToggle.IsOn = structureTuning.EnableFastAlignment;
        _fastShadowToggle.IsOn = structureTuning.FastAlignmentShadowMode;
        _visibleAwareToggle.IsOn = structureTuning.EnableVisibleMask
            && structureTuning.EnableVisibleAwareInjection;
        _featureRatioThreshold.Value = structureTuning.FeatureRatioThreshold * 100d;
        _featureInlierTolerance.Value = structureTuning.FeatureInlierTolerancePixels;
        _featureMaxCandidates.Value = structureTuning.MaximumTranslationCandidates;
        _fastCoarseDownsample.Value = structureTuning.FastCoarseDownsampleFactor;
        _fastCoarseTopK.Value = structureTuning.FastCoarseTopK;
        _structureOccupancy.Value = structureTuning.MinimumOccupancyCoverage * 100d;
        _structurePartitions.Value = structureTuning.MinimumConsistentPartitions;
        _structureEdgeTolerance.Value = structureTuning.EdgeDistanceTolerancePixels;
        _structureTopCandidates.Value = structureTuning.TopCandidateCount;
        _structureBudget.Value = structureTuning.StructureFallbackBudgetMilliseconds;
        _firstScanStrategyToggle.IsOn =
            _runtime.Settings.FirstScanStrategy == FirstScanStrategy.SideEntrance;
        _sideEntranceFeatureRadius.Value = tuning.SideEntranceFeatureRadius;
        var controlsEnabled = !_runtime.IsScanning;
        _gateThreshold.IsEnabled = controlsEnabled;
        _minimumConfidence.IsEnabled = controlsEnabled;
        _highConfidenceThreshold.IsEnabled = controlsEnabled;
        _vectorTolerance.IsEnabled = controlsEnabled;
        _ambiguityMargin.IsEnabled = controlsEnabled;
        _confirmationAdvantage.IsEnabled = controlsEnabled;
        _forceBestResultToggle.IsEnabled = controlsEnabled;
        _forceCandidateToggle.IsEnabled = controlsEnabled;
        _skipFloorRecognitionToggle.IsEnabled = controlsEnabled;
        _skipStabilityConfirmationToggle.IsEnabled = controlsEnabled;
        _mediumConfidenceThreshold.IsEnabled = controlsEnabled;
        _openingAnimationDelay.IsEnabled = controlsEnabled;
        _openingTimeout.IsEnabled = controlsEnabled;
        _stableFrameCount.IsEnabled = controlsEnabled;
        _stableFrameInterval.IsEnabled = controlsEnabled;
        _stableFrameDifference.IsEnabled = controlsEnabled;
        _mediumConfidenceFrames.IsEnabled = controlsEnabled;
        _candidateStabilityPixels.IsEnabled = controlsEnabled;
        _nativeScaleChangeRatio.IsEnabled = controlsEnabled;
        _warmGateSearchBudget.IsEnabled = controlsEnabled;
        _confirmationGateSearchBudget.IsEnabled = controlsEnabled;
        _confirmationRoiPaddingFactor.IsEnabled = controlsEnabled;
        _confirmationRoiMinimumPadding.IsEnabled = controlsEnabled;
        _confirmationMaximumMapDrag.IsEnabled = controlsEnabled;
        _floorMinimumConfidence.IsEnabled = controlsEnabled;
        _floorLocalizationConfidence.IsEnabled = controlsEnabled;
        _floorRecognitionTimeout.IsEnabled = controlsEnabled;
        _floorFirstConfirmationFrames.IsEnabled = controlsEnabled;
        _floorSecondConfirmationFrames.IsEnabled = controlsEnabled;
        _playerMinimumConfidence.IsEnabled = controlsEnabled;
        _playerFailureLimit.IsEnabled = controlsEnabled;
        _playerStaleHideMilliseconds.IsEnabled = controlsEnabled;
        _allowExtendToggle.IsEnabled = controlsEnabled;
        _miniMapEnabledToggle.IsEnabled = controlsEnabled;
        _miniMapScale.IsEnabled =
            controlsEnabled && _miniMapEnabledToggle.IsOn;
        _auxiliaryAnchorToggle.IsEnabled = controlsEnabled;
        _reusePreviousAlignmentToggle.IsEnabled = controlsEnabled;
        _previousAlignmentSearchRadius.IsEnabled =
            controlsEnabled && _reusePreviousAlignmentToggle.IsOn;
        _structureEccToggle.IsEnabled = controlsEnabled;
        _structureDebugToggle.IsEnabled = controlsEnabled;
        _structureChamfer.IsEnabled = controlsEnabled;
        _structureCoverage.IsEnabled = controlsEnabled;
        _structureMargin.IsEnabled = controlsEnabled;
        _auxiliaryMaxTemplates.IsEnabled =
            controlsEnabled && _auxiliaryAnchorToggle.IsOn;
        _auxiliaryDirectLockConfidence.IsEnabled =
            controlsEnabled && _auxiliaryAnchorToggle.IsOn;
        _viewportEdgeMargin.IsEnabled = controlsEnabled;
        _featureVotingToggle.IsEnabled = controlsEnabled;
        _visibleAwareToggle.IsEnabled = controlsEnabled;
        _featureRatioThreshold.IsEnabled =
            controlsEnabled && _featureVotingToggle.IsOn;
        _featureInlierTolerance.IsEnabled =
            controlsEnabled && _featureVotingToggle.IsOn;
        _featureMaxCandidates.IsEnabled =
            controlsEnabled && _featureVotingToggle.IsOn;
        _fastCoarseDownsample.IsEnabled = controlsEnabled;
        _fastCoarseTopK.IsEnabled = controlsEnabled;
        _structureOccupancy.IsEnabled = controlsEnabled;
        _structurePartitions.IsEnabled = controlsEnabled;
        _structureEdgeTolerance.IsEnabled = controlsEnabled;
        _structureTopCandidates.IsEnabled = controlsEnabled;
        _structureBudget.IsEnabled = controlsEnabled;
        _alignmentMode.IsEnabled = controlsEnabled;
        _firstScanStrategyToggle.IsEnabled = controlsEnabled;
        _sideEntranceFeatureRadius.IsEnabled = controlsEnabled;
        _scanButton.IsEnabled =
            controlsEnabled && _runtime.MatchSnapshot.IsStarted;
        _manualButton.IsEnabled =
            controlsEnabled && _runtime.MatchSnapshot.IsStarted;
        _refreshing = false;

        _gameMapBinding.Text = $"当前：{_runtime.Settings.GameMapToggleBinding.DisplayName}";
        _controlPanelBinding.Text =
            $"当前：{_runtime.Settings.ControlPanelToggleBinding.DisplayName}";
        _quickBinding.Text = $"当前：{_runtime.Settings.QuickScanBinding.DisplayName}";
        _overlayBinding.Text = $"当前：{_runtime.Settings.OverlayToggleBinding.DisplayName}";
        _manualBinding.Text = $"当前：{_runtime.Settings.ManualRecognitionBinding.DisplayName}";
        _switchFloorBinding.Text = $"当前：{_runtime.Settings.SwitchFloorBinding.DisplayName}";
        _overlayState.Text = _runtime.IsOverlayVisible
            ? "正在显示（鼠标与键盘穿透）"
            : "当前隐藏";
        _permissionState.Text = _runtime.IntegrityStatus.Message;
        _elevationButton.Visibility = _runtime.IntegrityStatus.RequiresElevation
            ? Visibility.Visible
            : Visibility.Collapsed;
        _calibrationState.Text = _runtime.Settings.IsMapViewportCalibrated
            ? $"已校准（基准 {_runtime.Settings.CalibrationClientWidth}×{_runtime.Settings.CalibrationClientHeight}）"
            : _runtime.Settings.MapViewportRegion?.IsValid is true
                ? "需要重新校准（旧校准区域已保留）"
                : "尚未校准";
        _floorCalibrationState.Text = _runtime.Settings.IsFloorDisplayCalibrated
            ? $"已校准（基准 {_runtime.Settings.FloorCalibrationClientWidth}×{_runtime.Settings.FloorCalibrationClientHeight}）"
            : _runtime.Settings.FloorDisplayRegion?.IsValid is true
                ? "需要重新校准（旧校准区域已保留）"
                : "尚未校准";
        _playerCalibrationState.Text = _runtime.ArePlayerAssetsReady
            ? "4/4 张序号图片已就绪"
            : "玩家序号图片不完整；无法开始对局";
        _floorState.Text = _runtime.LastFloorRecognition is { } floorResult
            ? floorResult.Succeeded && floorResult.Floor is { } floor
                ? $"{floor.ToUpperInvariant()} · 置信度 {floorResult.Confidence:P0}"
                    + $" · 数字定位 {floorResult.LocalizationConfidence:P0}"
                    + (floorResult.LocalizedRegion is { } localized
                        ? $" · 内部区域 "
                            + $"{localized.X:P0},{localized.Y:P0} "
                            + $"{localized.Width:P0}×{localized.Height:P0}"
                        : string.Empty)
                    + $" · 捕获 {floorResult.CaptureMilliseconds:F1}ms"
                    + $" · 判定 {floorResult.AnalysisMilliseconds:F1}ms"
                    + $" · 端到端 {floorResult.EndToEndMilliseconds:F1}ms"
                    + (floorResult.EndToEndMilliseconds
                            > MapFloorRecognitionRules.PerformanceBudgetMilliseconds
                        ? " · 超过 100ms 性能目标"
                        : string.Empty)
                : $"未识别 · {floorResult.FailureReason}"
                    + $" · 端到端 {floorResult.EndToEndMilliseconds:F1}ms"
            : "尚无楼层识别结果";
        _mapReadiness.Text =
            $"{_runtime.ReadyMapCount}/{_runtime.TotalMapCount} 张地图已完成一楼区域与双门标记";
        _selectedMapState.Text = _runtime.SelectedMap is { } selectedMap
            ? selectedMap.DisplayName
            : "尚未快捷扫描或手动选择地图";
        _alignmentState.Text = FormatAlignmentState(
            _runtime.AlignmentTrackingMode);
        var snapshot = _runtime.SessionSnapshot;
        _sessionState.Text = FormatSessionSnapshot(snapshot);
        var match = _runtime.MatchSnapshot;
        _matchState.Text = match.IsStarted
            ? $"已开始 · 自己是 {(int)match.PlayerSlot!.Value} 号玩家 · 模式 {match.MapClass} · 版本 {match.Version}"
            : $"已结束 · 未选择玩家 · 版本 {match.Version}";
        _controlPanelState.Text = _runtime.IsControlPanelVisible
            ? "正在显示（可交互）"
            : "当前隐藏";
        _playerState.Text = snapshot.Player is { IsTrusted: true } player
            ? $"{(int)player.PlayerSlot} 号 · 完整地图 ({player.ReferencePoint.X:F1}, {player.ReferencePoint.Y:F1})"
                + $" · 视口 ({player.ViewportPoint.X:F1}, {player.ViewportPoint.Y:F1})"
                + $" · 置信度 {player.Confidence:P0}"
            : _runtime.LastTrustedPlayerPosition is { } prior
                ? $"当前图标隐藏；最后可信完整地图位置 ({prior.X:F1}, {prior.Y:F1})"
                : "尚无可信玩家位置";
        _lastResult.Text = _runtime.LastRecognition is { } recognition
            ? FormatRecognition(recognition)
            : "尚无识别结果";
        _timings.Text = _runtime.LastDiagnostics is { } diagnostics
            ? diagnostics.ToStatusText()
                + (diagnostics.StructureSearchMilliseconds > 0d
                    ? $" · 候选 {diagnostics.StructureCandidateCount}"
                        + $" · 最佳/次佳 {diagnostics.StructureBestScore:F3}/{diagnostics.StructureSecondScore:F3}"
                        + $" · 差距 {diagnostics.StructureCandidateMargin:P1}"
                        + $" · AKAZE 内点 {diagnostics.StructureFeatureInlierCount}/{diagnostics.StructureFeatureMatchCount}"
                        + $" · 聚类 {diagnostics.StructureFeatureConsensus:P0}"
                        + (diagnostics.StructureEccConverged
                            ? $" · ECC {diagnostics.StructureEccCorrelation:F3}"
                            : " · ECC 未收敛")
                    : string.Empty)
            : "尚无扫描数据";
        _collectLogsToggle.IsOn = _runtime.Settings.CollectLogs;
        _collectResearchToggle.IsOn =
            _runtime.Settings.CollectAlignmentResearchData;
        _allowExtendToggle.IsOn = _runtime.Settings.AllowMapExtendBeyondBounds;
        _miniMapEnabledToggle.IsOn = _runtime.Settings.PersistentMiniMapEnabled;
        _playerTrackingToggle.IsOn = _runtime.Settings.PlayerTrackingEnabled;
        _reverseAlternateDisplayToggle.IsOn = _runtime.Settings.ReverseAlternateDisplay;
        _reverseAlternateDisplayToggle.IsEnabled = controlsEnabled
            && !_overlayStatusToggle.IsOn;
        _miniMapScale.Value = _runtime.Settings.MiniMapScale * 100d;
        _showGateMarkersToggle.IsOn = _runtime.Settings.ShowGateMarkers;
        _showAuxiliaryAnchorsToggle.IsOn = _runtime.Settings.ShowAuxiliaryAnchors;
        _showTextAnnotationsToggle.IsOn = _runtime.Settings.ShowTextAnnotations;
        _showBoxAnnotationsToggle.IsOn = _runtime.Settings.ShowBoxAnnotations;
        _showGateMarkersOnMiniMapToggle.IsOn = _runtime.Settings.ShowGateMarkersOnMiniMap;
        _showAuxiliaryAnchorsOnMiniMapToggle.IsOn = _runtime.Settings.ShowAuxiliaryAnchorsOnMiniMap;
        _showTextAnnotationsOnMiniMapToggle.IsOn = _runtime.Settings.ShowTextAnnotationsOnMiniMap;
        _showBoxAnnotationsOnMiniMapToggle.IsOn = _runtime.Settings.ShowBoxAnnotationsOnMiniMap;
        _showFloorOnMiniMapToggle.IsOn = _runtime.Settings.ShowFloorOnMiniMap;
        _miniMapScaleBox.Value = _runtime.Settings.MiniMapScale * 100d;
        _miniMapOpacityBox.Value = _runtime.Settings.MiniMapOpacity * 100d;
        _miniMapOffsetXBox.Value = _runtime.Settings.MiniMapOffsetX;
        _miniMapOffsetYBox.Value = _runtime.Settings.MiniMapOffsetY;
        _statusOpacityBox.Value = _runtime.Settings.StatusOpacity * 100d;
        _statusOffsetXBox.Value = _runtime.Settings.StatusOffsetX;
        _statusOffsetYBox.Value = _runtime.Settings.StatusOffsetY;
        _logState.Text = _runtime.LogCollector.IsEnabled
            ? $"正在收集 · {_runtime.LogCollector.EntryCount} 条"
            : "已关闭";
        _researchState.Text = _runtime.ResearchCollector.IsEnabled
            ? $"正在采集 · {_runtime.ResearchCollector.RecordCount} 条 · "
                + (_runtime.ResearchCollector.CurrentSessionDirectory
                    ?? _runtime.ResearchCollector.RootDirectory)
            : $"已关闭 · {_runtime.ResearchCollector.RootDirectory}";
        _status.Text = _runtime.StatusMessage;
    }

    private static string FormatSessionSnapshot(MapSessionSnapshot snapshot)
    {
        var summary =
            $"{snapshot.State} · {snapshot.LocationMethod}"
            + $" · 会话版本 {snapshot.Version}"
            + $" · 对齐修订 {snapshot.AlignmentRevision}"
            + (snapshot.MapId is { } mapId
                ? $" · 地图 {mapId.ToString("N")[..8]}"
                : string.Empty)
            + (snapshot.Floor is { } floor
                ? $" · {floor.ToUpperInvariant()}"
                : string.Empty)
            + (snapshot.State == MapSessionState.LowConfidence
                    || snapshot.Confidence > 0d
                ? $" · 置信度 {snapshot.Confidence:P1}"
                : string.Empty)
            + (snapshot.StableCandidateFrames > 0
                ? $" · 稳定帧 {snapshot.StableCandidateFrames}"
                : string.Empty);
        if (snapshot.ViewportOrigin is { } origin)
            summary += $" · 视口原点 ({origin.X:F1}, {origin.Y:F1})";
        if (snapshot.LockedTransform is { } transform)
        {
            summary +=
                $" · S={transform.Scale:F4}"
                + $" R={transform.RotationDegrees:F1}°"
                + $" T=({transform.TranslationX:F1}, {transform.TranslationY:F1})";
        }
        if (snapshot.RecalibrationReason != MapRecalibrationReason.None)
            summary += $" · 失效原因 {snapshot.RecalibrationReason}";
        if (!string.IsNullOrWhiteSpace(snapshot.Detail))
            summary += $" · {snapshot.Detail}";
        return summary;
    }

    private static string FormatRecognition(RuntimeMapRecognition recognition)
    {
        var source = recognition.Result.Source switch
        {
            MapRecognitionSource.ManualGateSelection => "手动门点",
            MapRecognitionSource.UserConfirmed => "手动确认",
            MapRecognitionSource.SelectedMapGatePair => "双门完整对齐",
            MapRecognitionSource.SingleGateTracking => "单门跟踪（缩放锁定）",
            MapRecognitionSource.AuxiliaryAnchorTracking => "辅助锚点跟踪（缩放锁定）",
            MapRecognitionSource.StructureMatching => "局部地图结构配准",
            MapRecognitionSource.ReusedLastTransform => "复用上次可靠对齐",
            _ => "自动识别"
        };
        var summary =
            $"{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()} · {recognition.Result.Confidence:P0} · {source}"
            + $" · 证据 {recognition.Result.EvidenceKind}"
            + (recognition.Result.SkippedStructureValidation
                ? " · 已跳过结构复核"
                : $" · 结构 {recognition.Result.StructureDisposition}")
            + (recognition.Result.WasForcedBestResult
                ? " · 强制呈现"
                : string.Empty);
        if (recognition.Result.OverlayTransform is not { } transform)
            return summary;
        return summary
            + $" · {transform.AlignmentMode.ToDisplayName()}"
            + (recognition.Result.Source == MapRecognitionSource.StructureMatching
                ? $" · 平均边缘距离 {transform.MaximumResidualPixels:F1}px"
                : $" · 最大误差 {transform.MaximumResidualPixels:F1}px")
            + (recognition.Result.Source == MapRecognitionSource.StructureMatching
                ? $" · 候选差距 {recognition.Result.StructureCandidateMargin:P1}"
                : string.Empty)
            + (transform.UsedDegenerateAxisFallback ? " · 退化轴回退" : string.Empty)
            + (transform.IsExactFit ? " · 已贴合" : " · 未完全贴合");
    }

    private static string FormatAlignmentState(
        MapAlignmentTrackingMode mode) => mode switch
    {
        MapAlignmentTrackingMode.NeedsGatePair => "需要双门完成本次运行的缩放锁定",
        MapAlignmentTrackingMode.GatePairLocked => "双门完整对齐",
        MapAlignmentTrackingMode.SingleGateTracking => "单门跟踪（缩放锁定）",
        MapAlignmentTrackingMode.AuxiliaryAnchorTracking => "辅助锚点跟踪（缩放锁定）",
        MapAlignmentTrackingMode.WaitingForAnchor => "等待可信门点或结构证据恢复",
        MapAlignmentTrackingMode.StructureMatched => "局部地图结构配准",
        MapAlignmentTrackingMode.HoldingLastTransform => "结构证据不足，已保留最后可靠对齐",
        MapAlignmentTrackingMode.Lost => "对齐已失效，需要双门重新锁定",
        _ => "尚无运行时对齐"
    };
}
