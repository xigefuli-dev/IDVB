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
    private readonly GameOverlayProgressBar _scanProgressOverlay = new();

    // Session state
    private readonly MapOpenSession _mapOpenSession = new();
    private readonly MapCandidateStabilityTracker _candidateStability = new();
    private readonly MapAlignmentCommitGuard _alignmentCommitGuard = new();
    private readonly MapGameToggleState _gameMapToggleState = new();
    private readonly MapMatchSession _matchSession = new();

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private bool _disposed;
    private bool _initialized;
    private MapRuntimeSettings? _settings;
    private string _statusMessage = "就绪";
    private bool _elevationEventRaised;
    private bool _manualSelectionActive;
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
    private MapReferencePoint? _lastTrustedPlayerPoint;
    private string? _currentFloorKey;
    private MapAlignmentTrackingMode _alignmentTrackingMode = MapAlignmentTrackingMode.None;
    private MapScreenRect _lastGameBounds;
    private IntPtr _lastGameWindowHandle;
    private readonly bool _headless;

    // ════════════════ Constructor ════════════════

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
        bool headless = false)
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
                (slot, mapClass) => BeginMatchAsync(slot, mapClass),
                GetMapClassesAsync,
                () => _settings?.AllowAutomaticMapCache is true,
                saveAutomaticMapCache =>
                    EndMatchAsync(saveAutomaticMapCache),
                () => _surveyCoordinator.Status);
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
            await _mapFeatureCacheRepository.InitializeAsync();
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
    public MapSessionSnapshot SessionSnapshot => _mapOpenSession.Snapshot;
    public RuntimeMapRecognition? LastRecognition => _lastRecognition;
    public MapAlignmentSession? LastAlignmentSession => _lastAlignmentSession;
    public MapScanDiagnostics? LastDiagnostics => _lastDiagnostics;

    /// <summary>上次扫描管线的各阶段耗时（键=阶段名，值=毫秒）。</summary>
    public IReadOnlyDictionary<string, double>? LastScanPhaseTimings => _lastScanPhaseTimings;
    public IReadOnlyDictionary<string, double>? LastAlignmentPhaseTimings => _lastAlignmentPhaseTimings;
    public MapFloorRecognitionResult? LastFloorRecognition => _lastFloorRecognition;
    public MapReferencePoint? LastTrustedPlayerPosition => _lastTrustedPlayerPoint;
    public MapAlignmentTrackingMode AlignmentTrackingMode => _alignmentTrackingMode;
    public int ReadyMapCount => _recognition.ReadyMapCount;
    public int SideEntranceReadyMapCount => _recognition.SideEntranceReadyMapCount;
    public int TotalMapCount => _recognition.TotalMapCount;
    public bool ArePlayerAssetsReady => false;
    public GameIntegrityStatus IntegrityStatus { get; private set; } =
        new(false, false, false, false, "尚未检查。");

    // ════════════════ Events ════════════════

    public event EventHandler? StateChanged;
    public event EventHandler? ElevationRequiredDetected;

    // ════════════════ ISessionOrchestrator ════════════════

    public Task BeginMatchAsync() => Task.CompletedTask;
    public async Task RunScanAsync() => await Task.CompletedTask;

    public async Task BeginMatchAsync(PlayerSlot playerSlot, string mapClass)
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            if (_matchSession.Snapshot.IsStarted)
                throw new InvalidOperationException("A match is already in progress.");
            ResetMatchTransientState(resetAutomaticCacheSamples: true);
            StartMatchCancellationScope();
            var match = _matchSession.Begin(playerSlot, mapClass);
            _statusMessage =
                $"对局已开始 · {mapClass} · 槽位 {(int)playerSlot}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"进入对局 · version={match.Version} · class={match.MapClass} "
                + $"· slot={(int)playerSlot}");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
    }

    public Task EndMatchAsync() => EndMatchAsync(saveAutomaticMapCache: false);

    private async Task EndMatchAsync(bool saveAutomaticMapCache)
    {
        await _matchLifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            var endingMatch = _matchSession.Snapshot;
            if (!endingMatch.IsStarted)
                return;

            Volatile.Write(ref _matchEnding, 1);
            // Invalidate the match identity first. Any scan/alignment already
            // running may finish native work, but can no longer commit state.
            _matchSession.End();
            CancelMatchOperations();
            var isSurvey = endingMatch.Mode == MapRunMode.Survey;
            _statusMessage = isSurvey
                ? "正在结束测绘对局并保存测绘项目……"
                : saveAutomaticMapCache
                    ? "正在结束对局并保存地图缓存……"
                    : "正在结束对局并丢弃本局地图缓存样本……";
            StateChanged?.Invoke(this, EventArgs.Empty);

            await DrainMatchOperationsAsync();
            await DrainMapCacheWritesAsync();
            await DrainAdaptiveScaleAsync();
            if (!isSurvey && saveAutomaticMapCache)
                await FlushAutomaticMapCacheAsync();
            else
                DiscardAutomaticMapCacheSamples(isSurvey
                    ? "测绘对局不使用普通地图缓存样本"
                    : "用户选择不保存或退出路径无法确认");
            await EndSurveyMatchAsync(endingMatch);
            ResetMatchTransientState(resetAutomaticCacheSamples: true);

            _statusMessage = "对局已结束。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"退出对局完成 · version={endingMatch.Version} · "
                + (isSurvey
                    ? "测绘项目已保存，普通地图缓存样本已静默丢弃"
                    : saveAutomaticMapCache
                    ? "本局任务已排空，自动地图缓存已完成确认落盘阶段"
                    : "本局任务已排空，自动地图缓存样本未保存"));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Volatile.Write(ref _matchEnding, 0);
            _matchLifecycleGate.Release();
        }
    }

    /// <summary>
    /// 用最新识别结果重建对齐会话。侧门扫描锁定的结果来源是
    /// <see cref="MapRecognitionSource.StructureMatching"/>（而非
    /// SideEntranceSelection），直接从结果重建会丢失侧门先验并错误地设置
    /// HasGatePairLock=true。这里在识别结果自然更新会话的同时，保留上一会话
    /// 的侧门身份先验，使后续"仅对齐"仍走允许缩放搜索的侧门路线。
    /// </summary>
    private static MapAlignmentSession UpdateAlignmentSession(
        MapAlignmentSession? previous,
        RuntimeMapRecognition recognition)
    {
        var rebuilt = MapAlignmentSession.FromRecognition(
            recognition.Map,
            recognition.Result);
        if (previous is null
            || previous.SideEntranceScanPriorConfidence <= 0d
            || rebuilt.SideEntranceScanPriorConfidence > 0d
            || previous.MapId != rebuilt.MapId
            || previous.MapUpdatedAt != rebuilt.MapUpdatedAt)
        {
            return rebuilt;
        }

        return new MapAlignmentSession
        {
            MapId = rebuilt.MapId,
            MapUpdatedAt = rebuilt.MapUpdatedAt,
            FloorKey = rebuilt.FloorKey,
            LockedTransform = rebuilt.LockedTransform,
            LockedGateEvidence = previous.SideEntranceScanPriorConfidence > 0d
                && previous.LockedGateEvidence.Count > 0
                ? previous.LockedGateEvidence
                : rebuilt.LockedGateEvidence,
            BaselineGateScale = rebuilt.BaselineGateScale,
            LastConfidence = rebuilt.LastConfidence,
            LastBestScore = rebuilt.LastBestScore,
            LastSecondScore = rebuilt.LastSecondScore,
            LastCandidateMargin = rebuilt.LastCandidateMargin,
            LastRejectionReason = rebuilt.LastRejectionReason,
            LastObservationConfidence = rebuilt.LastObservationConfidence,
            LastObservationBestScore = rebuilt.LastObservationBestScore,
            LastObservationSecondScore = rebuilt.LastObservationSecondScore,
            LastObservationCandidateMargin = rebuilt.LastObservationCandidateMargin,
            LastObservationRejectionReason = rebuilt.LastObservationRejectionReason,
            LastObservationAt = rebuilt.LastObservationAt,
            ConsecutiveRejections = rebuilt.ConsecutiveRejections,
            LastSuccessfulAt = rebuilt.LastSuccessfulAt,
            HasGatePairLock = false,
            SideEntranceScanPriorConfidence = previous.SideEntranceScanPriorConfidence,
            Mode = rebuilt.Mode,
            LastStructureAttempted = rebuilt.LastStructureAttempted,
            LastStructureAccepted = rebuilt.LastStructureAccepted,
            LastStructureFailureReason = rebuilt.LastStructureFailureReason,
            ConsecutiveStructureFailures = rebuilt.ConsecutiveStructureFailures,
            LastSearchStage = rebuilt.LastSearchStage,
        };
    }

    private void RememberPrimaryFloorSession(
        RuntimeMapRecognition recognition,
        MapAlignmentSession? session)
    {
        if (session is null)
            return;
        if (string.Equals(
                recognition.Result.Floor,
                MapFloorRules.GetPrimaryFloorKey(recognition.Map),
                StringComparison.Ordinal))
        {
            _primaryFloorAlignmentSession = session;
            RememberAdaptiveReliableKey(recognition, primary: true);
        }
    }

}
