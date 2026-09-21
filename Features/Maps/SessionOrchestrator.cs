// IDVB Remaster — Session Orchestrator（新架构唯一入口）
using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Survey.Contracts;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator : ISessionOrchestrator, IDisposable, IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly ISettingsRepository _settingsRepo;
    private readonly IMapRepository _mapRepo;
    private readonly IGameWindowCapture _captureSvc;
    private readonly IOverlayWindow _overlay;
    private readonly IGlobalInput _input;
    private readonly IGateDetector _gateDetector;
    private readonly IFloorRecognizer _floorRecognizer;
    private readonly IMapIdentifier _mapIdentifier;
    private readonly IStructureRegistrar _structureRegistrar;
    private readonly IPlayerMarkerDetector _playerMarkerSvc;
    private readonly IConfigProvider _config;
    private readonly IResolutionProfileService _profileService;
    private readonly PipelineFactory _pipelineFactory;
    private readonly ISurveyCoordinator _surveyCoordinator;
    private readonly SurveyCaptureTuning _surveyCaptureTuning;

    // Internal concrete services
    private readonly MapRepository _mapRepository;
    private readonly MapRuntimeSettingsRepository _rtSettingsRepo;
    private readonly MapCvRecognitionService _recognition;
    private readonly MapPlayerMarkerDetector _playerMarkerDetector;
    private readonly MapAlignmentResearchCollector _researchCollector;
    private readonly MapLogCollector _logCollector;
    private readonly MapRecognitionStatisticsRepository _recognitionStatsRepo;
    private readonly MapFeatureCacheRepository _mapFeatureCacheRepository;
    private readonly MapOverlayStatusCoordinator _overlayStatus;
    private readonly MapControlPanelWindow? _controlPanel;
    private readonly ICaptureProtectionService? _captureProtection;
    private readonly GameOverlayProgressBar _scanProgressOverlay;
    private readonly RealtimeMapTransformPublisher _realtimeMapTransformPublisher;

    // Session state
    private readonly MapOpenSession _mapOpenSession = new();
    private readonly MapCandidateStabilityTracker _candidateStability = new();
    private readonly MapAlignmentCommitGuard _alignmentCommitGuard = new();
    private readonly MapGameToggleState _gameMapToggleState = new();
    private readonly LowStructureRecoveryCursor _lowStructureRecoveryCursor = new();
    private readonly MapMatchSession _matchSession = new();
    private readonly MapMatchMapLease _mapLease = new();
    private readonly object _mapViewportReferenceGate = new();
    private readonly Dictionary<MapViewportReferenceKey, MapViewportColorSignature>
        _mapViewportReferences = [];

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _disposed;
    private bool _initialized;
    private MapRuntimeSettings? _settings;
    private string _statusMessage = "就绪";
    private bool _elevationEventRaised;
    private bool _manualSelectionActive;
    private bool _matchPluginsActivated;
    private int _activeScanOperations;

    // TODO: 扫描/对齐逻辑实现后填充以下字段
    private RuntimeMapRecognition? _lastRecognition;
    private MapAlignmentSession? _lastAlignmentSession;
    // Keep the primary-floor lock separate while the user views another
    // floor.  Returning to the primary floor must reuse its side-door seed,
    // not the transform found for the secondary floor.
    private MapAlignmentSession? _primaryFloorAlignmentSession;
    private MapScanDiagnostics? _lastDiagnostics;
    private IReadOnlyDictionary<string, double>? _lastScanPhaseTimings;
    private IReadOnlyDictionary<string, double>? _lastAlignmentPhaseTimings;
    private string? _lastStableCaptureFailureReason;
    private MapFloorRecognitionResult? _lastFloorRecognition;
    private ScanPipelineContext? _lastScanPipelineContext;
    private SideEntranceScanResult? _lastSideEntranceScan;
    private MapReferencePoint? _lastTrustedPlayerPoint;
    private string? _currentFloorKey;
    private MapAlignmentTrackingMode _alignmentTrackingMode = MapAlignmentTrackingMode.None;
    private MapScreenRect _lastGameBounds;
    private IntPtr _lastGameWindowHandle;
    private readonly bool _headless;

    public SessionOrchestrator(
        DispatcherQueue dispatcher,
        ISettingsRepository settingsRepo,
        IMapRepository mapRepo,
        IGameWindowCapture capture,
        IOverlayWindow overlay,
        IGlobalInput input,
        IGateDetector gateDetector,
        IFloorRecognizer floorRecognizer,
        IMapIdentifier mapIdentifier,
        IStructureRegistrar structureRegistrar,
        IPlayerMarkerDetector playerMarker,
        IConfigProvider config,
        IResolutionProfileService profileService,
        PipelineFactory pipelineFactory,
        MapAlignmentResearchCollector researchCollector,
        ISurveyCoordinator surveyCoordinator,
        SurveyCaptureTuning? surveyCaptureTuning = null,
        ICaptureProtectionService? captureProtection = null,
        bool headless = false,
        IMapCandidateLearningEngine? learningEngine = null)
    {
        _dispatcher = dispatcher;
        _settingsRepo = settingsRepo;
        _mapRepo = mapRepo;
        _captureSvc = capture;
        _overlay = overlay;
        _input = input;
        _gateDetector = gateDetector;
        _floorRecognizer = floorRecognizer;
        _mapIdentifier = mapIdentifier;
        _structureRegistrar = structureRegistrar;
        _playerMarkerSvc = playerMarker;
        _config = config;
        _profileService = profileService;
        _pipelineFactory = pipelineFactory;
        _surveyCoordinator = surveyCoordinator;
        _surveyCaptureTuning = surveyCaptureTuning ?? new SurveyCaptureTuning();
        _surveyCaptureTuning.Validate();
        _captureProtection = captureProtection;
        _scanProgressOverlay = new GameOverlayProgressBar(_captureProtection);
        _realtimeMapTransformPublisher = new RealtimeMapTransformPublisher(
            action => _dispatcher.TryEnqueue(() => action()),
            ApplyRealtimeMapTransform);
        _learningEngine = learningEngine ?? new MapSampleProviderEngine();
        _surveyCoordinator.StatusChanged += SurveyCoordinator_StatusChanged;
        _headless = headless;

        // Create internal concrete services
        _mapRepository = new MapRepository();
        _rtSettingsRepo = new MapRuntimeSettingsRepository();
        _recognition = new MapCvRecognitionService(_mapRepository);
        _playerMarkerDetector = new MapPlayerMarkerDetector();
        _researchCollector = researchCollector;
        _logCollector = new MapLogCollector();
        // Recognition helpers use the process-wide collector for structured
        // diagnostics.  RealCLI creates one orchestrator per input image, so
        // each new session must rebind that fallback after the previous
        // session has been disposed.
        MapLogCollector.Instance = _logCollector;
        _recognitionStatsRepo = new MapRecognitionStatisticsRepository();
        _mapFeatureCacheRepository = new MapFeatureCacheRepository();
        _overlayStatus = new MapOverlayStatusCoordinator(
            _overlay,
            action =>
            {
                if (_dispatcher.TryEnqueue(() => action()))
                    return;
                _logCollector.Append(
                    MapLogCategory.System,
                    MapLogLevel.Warning,
                    "Overlay status dispatch rejected",
                    details: new()
                    {
                        ["outcome"] = "dispatch-rejected",
                        ["operation"] = "overlay-status-expiration"
                    });
            });
        InitializeAdaptiveScale();

        // MapControlPanelWindow 仅在 GUI 模式创建（headless CLI 跳过）
        if (!headless)
        {
            _controlPanel = new MapControlPanelWindow(
                mapClass => BeginMatchAsync(mapClass),
                GetMapClassesAsync,
                () => _settings?.LastSelectedMapClass,
                SetLastSelectedMapClassAsync,
                () => _settings?.AllowAutomaticMapCache is true,
                saveAutomaticMapCache =>
                    EndMatchAsync(saveAutomaticMapCache),
                () => _surveyCoordinator.Status,
                () => Lifecycle.MainProgramPreferences.Load().AllowSurveyMode,
                mapClass => BeginSurveyMatchAsync(mapClass),
                ActivateSurveyMatchAsync,
                GetCurrentVariantContextAsync,
                SwitchVariantAsync,
                _captureProtection,
                CorrectMapAsync);
        }

        // 全局输入事件仅在 GUI 模式订阅（headless CLI 无输入设备）
        if (!headless)
        {
            _input.QuickScanInvoked += (_, _) =>
                StartInputOperation("quick-scan", RunQuickScanAsync);
            _input.OverlayToggleInvoked += (_, _) =>
                RunInputAction("overlay-toggle", ToggleOverlay);
            _input.ManualRecognitionInvoked += (_, _) =>
                StartInputOperation(
                    "manual-recognition",
                    RunManualRecognitionAsync);
            _input.GameMapToggleInvoked += (_, _) =>
                StartInputOperation("game-map-toggle", HandleGameMapToggleAsync);
            _input.ControlPanelToggleInvoked += (_, _) =>
                RunInputAction("control-panel-toggle", ToggleControlPanel);
            _input.SwitchFloorInvoked += (_, _) =>
                RunInputAction("switch-floor", HandleSwitchFloorSafely);
            _input.SaveMapCacheInvoked += (_, _) =>
                StartInputOperation("save-map-cache", SaveCurrentMapCacheAsync);
            _input.RestMapDisplayInvoked += (_, _) =>
                StartInputOperation("rest-map-display", RestMapDisplayAsync);
        }
    }

    private void RunInputAction(string actionName, Action action)
    {
        LogInputHandlerOutcome(actionName, "handler-started");
        try
        {
            action();
            LogInputHandlerOutcome(actionName, "handler-completed");
        }
        catch (Exception exception)
        {
            LogInputHandlerOutcome(actionName, "handler-failed", exception);
        }
    }

    private void StartInputOperation(
        string actionName,
        Func<Task> operation)
    {
        LogInputHandlerOutcome(actionName, "handler-started");
        Task task;
        try
        {
            task = operation();
        }
        catch (Exception exception)
        {
            LogInputHandlerOutcome(actionName, "handler-failed", exception);
            return;
        }

        _ = ObserveInputOperationAsync(actionName, task);
    }

    private async Task ObserveInputOperationAsync(
        string actionName,
        Task operation)
    {
        try
        {
            await operation;
            LogInputHandlerOutcome(actionName, "handler-completed");
        }
        catch (Exception exception)
        {
            LogInputHandlerOutcome(actionName, "handler-failed", exception);
        }
    }

    private void LogInputHandlerOutcome(
        string actionName,
        string outcome,
        Exception? exception = null)
    {
        try
        {
            _logCollector.Append(
                MapLogCategory.System,
                exception is null ? MapLogLevel.Info : MapLogLevel.Error,
                $"Input handler: {actionName} · {outcome}",
                details: new()
                {
                    ["outcome"] = outcome,
                    ["action"] = actionName,
                    ["exceptionType"] = exception?.GetType().FullName,
                    ["exception"] = exception?.ToString()
                });
        }
        catch
        {
        }
    }

    // ════════════════ Initialize ════════════════

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await _initializeGate.WaitAsync();
        try
        {
            if (_initialized) return;
            await Task.Run(FloorIndicatorTemplateRegistry.Prepare);
            var settingsObj = await _settingsRepo.LoadAsync();
            _settings = settingsObj is MapRuntimeSettings s ? s : new MapRuntimeSettings();
            _logCollector.IsEnabled = _settings.CollectLogs;
            try
            {
                await _researchCollector.SetEnabledAsync(
                    _settings.CollectAlignmentResearchData);
            }
            catch (Exception exception)
            {
                _logCollector.Append(
                    MapLogCategory.System,
                    MapLogLevel.Warning,
                    "研究数据采集器初始化失败，保留已保存设置并继续启动。",
                    details: new()
                    {
                        ["enabled"] = _settings.CollectAlignmentResearchData,
                        ["exceptionType"] = exception.GetType().FullName,
                        ["exception"] = exception.ToString()
                    });
            }

            // 从 TOML 预设合并分辨率专属默认值（仅当用户未自定义时生效）
            // 不同分辨率预设提供不同的 VectorErrorTolerance / AmbiguityMargin 等默认值
            if (Math.Abs(_settings.RecognitionTuning.VectorErrorTolerance - 0.15d) < 0.0001d)
                _settings.RecognitionTuning.VectorErrorTolerance =
                    RecognitionConfigRules.VectorErrorTolerance;
            if (Math.Abs(_settings.RecognitionTuning.AmbiguityMargin - 0.015d) < 0.0001d)
                _settings.RecognitionTuning.AmbiguityMargin =
                    RecognitionConfigRules.AmbiguityMargin;

            await _recognition.RefreshCacheAsync();
            if (_settings.IsEnabled
                && !TryValidateEnablePrerequisites(out var prerequisiteFailure))
            {
                _settings.IsEnabled = false;
                await SaveSettingsAsync();
                System.Diagnostics.Debug.WriteLine(
                    "Map runtime was forced off: " + prerequisiteFailure);
            }
            await _mapFeatureCacheRepository.InitializeAsync();
            await InitializeLearningEngineAsync();
            await InitializeAdaptiveScaleAsync();
            await _surveyCoordinator.InitializeAsync(_lifetimeCts.Token);

            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Info,
                $"地图运行时已初始化 · 主识别 {_recognition.ReadyMapCount}/{_recognition.TotalMapCount} "
                + $"· 侧门 {_recognition.SideEntranceReadyMapCount}/{_recognition.TotalMapCount}",
                details: new()
                {
                    ["readyMapCount"] = _recognition.ReadyMapCount,
                    ["totalMapCount"] = _recognition.TotalMapCount,
                    ["sideEntranceReadyMapCount"] =
                        _recognition.SideEntranceReadyMapCount
                });

            ApplyBindings();
            ApplyDisplaySettingsToOverlay();

            _initialized = true;
            MapClassDiagnosticCoordinator.Instance.Trigger(_mapRepository);
            CheckIntegrityAndNotify();
        }
        finally { _initializeGate.Release(); }
    }

    // ════════════════ Preset Management ════════════════

    /// <summary>获取所有可用的分辨率预设。</summary>
    public IReadOnlyList<Core.Models.ResolutionTuningProfile> GetAvailablePresets()
        => _profileService.GetAvailableProfiles();

    /// <summary>获取当前活跃的预设名称。</summary>
    public string GetActivePreset()
        => _config.ActiveResolutionPreset;

    /// <summary>获取「使用配置文件」的用户选择；null/空 表示「自动」。</summary>
    public string? GetSelectedResolutionPreset()
        => _settings?.SelectedResolutionPreset;

    /// <summary>切换到指定分辨率预设，重新加载 TOML 配置并刷新叠加层显示。</summary>
    public async Task SetActivePresetAsync(string name)
    {
        EndAdaptiveMapOpen("resolution preset changed");
        ClearAdaptiveSessionKeys();
        CancelOrbTracking("resolution preset changed");
        await DrainOrbTrackingAsync();
        await _profileService.SetActiveProfileAsync(name);

        // 重新应用所有 TOML 规则
        GateTemplateRules.ApplyConfig(_config);
        RecognitionConfigRules.ApplyConfig(_config);
        StructureRegistrationRules.ApplyConfig(_config);
        SideEntranceScanRules.ApplyConfig(_config);
        OverlayDisplayRules.ApplyConfig(_config);

        // 刷新叠加层显示
        ApplyDisplaySettingsToOverlay();

        // 刷新识别缓存（结构参考可能依赖分辨率参数）
        await _recognition.RefreshCacheAsync();

        _logCollector.Append(
            MapLogCategory.System,
            MapLogLevel.Info,
            $"已切换到分辨率预设：{name}");

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ════════════════ Public Properties ════════════════
    public MapRuntimeSettings Settings => _settings ??= new MapRuntimeSettings();
    public MapRecord? SelectedMap => null;
    public string? CurrentFloorKey => _currentFloorKey;
    public (double Width, double Height)? CurrentMiniMapPixelSize =>
        _overlay.CurrentMiniMapWidth is { } width
        && _overlay.CurrentMiniMapHeight is { } height
        && width > 0d
        && height > 0d
            ? (width, height)
            : null;
    public double? CurrentMiniMapScale => _overlay.CurrentMiniMapScale;
    public bool IsOverlayVisible => _overlay.IsVisible;
    public bool IsGameMapOpen => _gameMapToggleState.IsOpen;
    public int GameMapToggleVersion => _gameMapToggleState.Version;
    public bool IsControlPanelVisible => _controlPanel?.IsVisible ?? false;
    public bool IsScanning => Volatile.Read(ref _activeScanOperations) > 0;
    public string StatusMessage => _statusMessage;
    public MapLogCollector LogCollector => _logCollector;
    public MapAlignmentResearchCollector ResearchCollector => _researchCollector;
    public ISurveyCoordinator SurveyCoordinator => _surveyCoordinator;
    public MapMatchSnapshot MatchSnapshot => _matchSession.Snapshot;
    public bool IsMatchStarted => _matchSession.Snapshot.IsStarted;
    public string? CurrentMatchId => _matchSession.Snapshot is { IsStarted: true }
        ? _matchSession.Snapshot.MatchId.ToString()
        : null;
    public MapSessionSnapshot SessionSnapshot => _mapOpenSession.Snapshot;
    public RuntimeMapRecognition? LastRecognition => _lastRecognition;
    /// <summary>仅确认了地图身份、尚无完整变换的待对齐识别（侧门/参考线索路径）。</summary>
    public RuntimeMapRecognition? PendingAlignmentIdentity => _pendingAlignmentIdentity;
    public MapAlignmentSession? LastAlignmentSession => _lastAlignmentSession;
    public MapScanDiagnostics? LastDiagnostics => _lastDiagnostics;

    /// <summary>上次扫描管线的各阶段耗时（键=阶段名，值=毫秒）。</summary>
    public IReadOnlyDictionary<string, double>? LastScanPhaseTimings => _lastScanPhaseTimings;
    public IReadOnlyDictionary<string, double>? LastAlignmentPhaseTimings => _lastAlignmentPhaseTimings;
    public MapFloorRecognitionResult? LastFloorRecognition => _lastFloorRecognition;
    public MapReferencePoint? LastTrustedPlayerPosition => _lastTrustedPlayerPoint;
    public MapAlignmentTrackingMode AlignmentTrackingMode => _alignmentTrackingMode;
    public ScanPipelineContext? LastScanPipelineContext => _lastScanPipelineContext;
    public SideEntranceScanResult? LastSideEntranceScan => _lastSideEntranceScan;
    public MapCvRecognitionService RecognitionService => _recognition;
    public MapRepository MapRepository => _mapRepository;
    public IVpsg3PreparedIndexRegistry Vpsg3Registry => _recognition.Vpsg3Registry;

    public async Task<bool> WaitForVpsg3PreparedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            var registry = _recognition.Vpsg3Registry;
            if (registry.Count > 0 && registry.BuildingCount == 0)
                return registry.ReadyCount > 0;

            if (registry.ReadyCount > 0 && registry.BuildingCount == 0)
                return true;

            await Task.Delay(50, cancellationToken);
        }
        return _recognition.Vpsg3Registry.ReadyCount > 0;
    }

    public int ReadyMapCount => _recognition.ReadyMapCount;
    public int SideEntranceReadyMapCount => _recognition.SideEntranceReadyMapCount;
    public int TotalMapCount => _recognition.TotalMapCount;
    public bool ArePlayerAssetsReady => false;
    public GameIntegrityStatus IntegrityStatus { get; private set; } =
        new(false, false, "尚未检查。");

    // ════════════════ Events ════════════════
    public event EventHandler? StateChanged;
    public event EventHandler? ElevationRequiredDetected;

}
