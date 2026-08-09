// IDVB Remaster — Session Orchestrator（新架构唯一入口）

using IDVBuff.Core.Contracts;
using IDVBuff.Core.Models;
using IDVBuff.Pipeline;
using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;

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
        _headless = headless;

        // Create internal concrete services
        _mapRepository = new MapRepository();
        _rtSettingsRepo = new MapRuntimeSettingsRepository();
        _recognition = new MapCvRecognitionService(_mapRepository);
        _playerMarkerDetector = new MapPlayerMarkerDetector();
        _researchCollector = new MapAlignmentResearchCollector();
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
            action => _dispatcher.TryEnqueue(() => action()));

        // MapControlPanelWindow 仅在 GUI 模式创建（headless CLI 跳过）
        if (!headless)
        {
            _controlPanel = new MapControlPanelWindow(
                (slot, mapClass) => BeginMatchAsync(slot, mapClass),
                GetMapClassesAsync,
                () => _settings?.AllowAutomaticMapCache is true,
                saveAutomaticMapCache =>
                    EndMatchAsync(saveAutomaticMapCache));
        }

        // 全局输入事件仅在 GUI 模式订阅（headless CLI 无输入设备）
        if (!headless)
        {
            _input.QuickScanInvoked += (_, _) =>
                _dispatcher.TryEnqueue(() => _ = RunQuickScanAsync());
            _input.OverlayToggleInvoked += (_, _) =>
                _dispatcher.TryEnqueue(ToggleOverlay);
            _input.ManualRecognitionInvoked += (_, _) =>
                _dispatcher.TryEnqueue(() => _ = RunManualRecognitionAsync());
            _input.GameMapToggleInvoked += (_, _) =>
                _dispatcher.TryEnqueue(() => _ = HandleGameMapToggleAsync());
            _input.ControlPanelToggleInvoked += (_, _) =>
                _dispatcher.TryEnqueue(ToggleControlPanel);
            _input.SwitchFloorInvoked += (_, _) =>
                _dispatcher.TryEnqueue(HandleSwitchFloor);
            _input.SaveMapCacheInvoked += (_, _) =>
                _dispatcher.TryEnqueue(() => _ = SaveCurrentMapCacheAsync());
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

            _logCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Info,
                $"地图运行时已初始化 · 就绪地图 {_recognition.ReadyMapCount}/{_recognition.TotalMapCount}",
                details: new()
                {
                    ["readyMapCount"] = _recognition.ReadyMapCount,
                    ["totalMapCount"] = _recognition.TotalMapCount
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

    /// <summary>切换到指定分辨率预设，重新加载 TOML 配置并刷新叠加层显示。</summary>
    public async Task SetActivePresetAsync(string name)
    {
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
            _statusMessage = saveAutomaticMapCache
                ? "正在结束对局并保存地图缓存……"
                : "正在结束对局并丢弃本局地图缓存样本……";
            StateChanged?.Invoke(this, EventArgs.Empty);

            await DrainMatchOperationsAsync();
            await DrainMapCacheWritesAsync();
            if (saveAutomaticMapCache)
                await FlushAutomaticMapCacheAsync();
            else
                DiscardAutomaticMapCacheSamples("用户选择不保存或退出路径无法确认");
            ResetMatchTransientState(resetAutomaticCacheSamples: true);

            _statusMessage = "对局已结束。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"退出对局完成 · version={endingMatch.Version} · "
                + (saveAutomaticMapCache
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
        }
    }

    private async Task<IReadOnlyList<string>> GetMapClassesAsync()
    {
        var snapshot = await _mapRepo.GetCatalogSnapshotAsync();
        return snapshot is MapCatalogSnapshot cs ? cs.Classes : Array.Empty<string>();
    }

    // ════════════════ Public Methods ════════════════

    public async Task RefreshMapCacheAsync(Guid? changedMapId = null)
    {
        await _recognition.RefreshCacheAsync(changedMapId);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RunQuickScanAsync() => RunQuickScanAsync(candidateSelector: null);

    public async Task RunQuickScanAsync(
        IMapCandidateSelector? candidateSelector)
    {
        _lastCandidateChoices = [];
        if (_disposed)
            return;
        _lastScanPhaseTimings = null;
        if (!_initialized || _settings is null)
        {
            ReportCliGuardFailure("地图运行时尚未初始化。", MapLogCategory.Session);
            return;
        }
        if (!_settings.IsEnabled)
        {
            ReportCliGuardFailure("地图识别功能已禁用。", MapLogCategory.Session);
            return;
        }
        if (!_matchSession.Snapshot.IsStarted)
        {
            _statusMessage = "请先在对局控件中点击“进入对局”，再执行扫描。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var operationMatch = _matchSession.Snapshot;
        if (!_captureSvc.TryGetForegroundClientBounds(
                out _, out _, out var failureReason))
        {
            ReportCliCaptureFailure(failureReason);
            return;
        }

        _activeCandidateSelector = candidateSelector;
        _statusMessage = "快速扫描中……";
        StateChanged?.Invoke(this, EventArgs.Empty);

        Interlocked.Increment(ref _activeScanOperations);
        StateChanged?.Invoke(this, EventArgs.Empty);
        var restoreOverlay = _overlay.IsVisible;
        if (restoreOverlay)
            _overlay.Hide();
        try
        {
            await RunRecognitionPipelineAsync();
        }
        finally
        {
            if (restoreOverlay
                && IsCurrentMatchOperation(operationMatch)
                && !_overlay.IsVisible)
                _overlay.Show();
            _activeCandidateSelector = null;
            Interlocked.Decrement(ref _activeScanOperations);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Runs the existing map-open alignment entry point against the locked
    /// recognition result.  It never invokes the scan/identification pipeline.
    /// </summary>
    public async Task RunAlignmentAsync()
    {
        if (_disposed)
            return;
        _lastAlignmentPhaseTimings = null;
        if (!_initialized || _settings is null)
        {
            ReportCliGuardFailure("地图运行时尚未初始化。", MapLogCategory.Session);
            return;
        }
        if (!_settings.IsEnabled)
        {
            ReportCliGuardFailure("地图识别功能已禁用。", MapLogCategory.Session);
            return;
        }

        if (!_matchSession.Snapshot.IsStarted)
        {
            _statusMessage = "请先在对局控件中点击“进入对局”，再执行对齐。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_lastRecognition is null && _pendingAlignmentIdentity is null)
        {
            _statusMessage = "尚未锁定地图，请先按快捷扫描键确认地图。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_gameMapToggleState.IsOpen)
        {
            _statusMessage = "游戏地图未打开，请先打开游戏地图后再执行对齐。";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_captureSvc.TryGetForegroundClientBounds(
                out _, out _, out var failureReason))
        {
            ReportCliCaptureFailure(failureReason);
            return;
        }

        var transition = new MapGameToggleTransition(
            IsOpen: true,
            Version: _gameMapToggleState.Version);
        Interlocked.Increment(ref _activeScanOperations);
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await RunMapOpenAlignmentAsync(transition);
        }
        finally
        {
            Interlocked.Decrement(ref _activeScanOperations);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Synchronizes the external game's map state without running a scan.</summary>
    public void SynchronizeExternalGameMapState(bool isOpen)
    {
        if (_gameMapToggleState.IsOpen == isOpen)
            return;
        _gameMapToggleState.SetOpenForExternalController(isOpen);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReportCliCaptureFailure(string failureReason)
    {
        _statusMessage = string.IsNullOrWhiteSpace(failureReason)
            ? "地图截图失败。"
            : failureReason;
        _logCollector.Append(
            MapLogCategory.ViewportCapture,
            MapLogLevel.Warning,
            _statusMessage);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TryValidateCliCaptureTarget()
    {
        if (_captureSvc.TryGetForegroundClientBounds(
                out _, out _, out var failureReason))
            return true;

        ReportCliCaptureFailure(failureReason);
        return false;
    }

    private void ReportCliGuardFailure(string message, MapLogCategory category)
    {
        _statusMessage = message;
        _logCollector.Append(category, MapLogLevel.Warning, message);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static IReadOnlyDictionary<string, double> BuildAlignmentPhaseTimings(
        MapScanDiagnostics? diagnostics,
        double wallClockMilliseconds)
    {
        if (diagnostics is null)
            return new Dictionary<string, double>
            {
                ["wall_clock"] = wallClockMilliseconds
            };

        return new Dictionary<string, double>
        {
            ["input_to_alignment_start"] = diagnostics.InputToAlignmentStartMilliseconds,
            ["opening_animation_wait"] = diagnostics.OpeningAnimationWaitMilliseconds,
            ["stable_viewport_wait"] = diagnostics.StableViewportWaitMilliseconds,
            ["stable_viewport_capture"] = diagnostics.StableViewportCaptureMilliseconds,
            ["alignment_capture"] = diagnostics.AlignmentCaptureMilliseconds,
            ["alignment_dispatch"] = diagnostics.AlignmentDispatchMilliseconds,
            ["reference_image_load"] = diagnostics.ReferenceImageLoadMilliseconds,
            ["reference_cache"] = diagnostics.ReferenceCacheMilliseconds,
            ["gate_detection"] = diagnostics.GateDetectionMilliseconds,
            ["live_structure_preprocess"] = diagnostics.LiveStructurePreprocessMilliseconds,
            ["structure_preprocess"] = diagnostics.StructurePreprocessMilliseconds,
            ["structure_search"] = diagnostics.StructureSearchMilliseconds,
            ["structure_refine"] = diagnostics.StructureRefineMilliseconds,
            ["session_commit"] = diagnostics.SessionCommitMilliseconds,
            ["overlay"] = diagnostics.OverlayMilliseconds,
            ["alignment_pipeline"] = diagnostics.AlignmentPipelineMilliseconds,
            ["algorithm_total"] = diagnostics.TotalMilliseconds,
            ["wall_clock"] = wallClockMilliseconds
        };
    }

    public async Task RunManualRecognitionAsync()
    {
        if (_disposed || !_settings!.IsEnabled)
            return;
        var operationMatch = _matchSession.Snapshot;
        if (!operationMatch.IsStarted || IsMatchEnding)
            return;
        var cancellationToken = CurrentMatchCancellationToken;
        if (!_captureSvc.TryGetForegroundClientBounds(out _, out _, out _))
            return;

        if (!await _scanGate.WaitAsync(0))
        {
            _statusMessage = "已有扫描正在进行，请稍候。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            await RunManualRecognitionCoreAsync(
                operationMatch,
                cancellationToken);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"手动识别已取消 · matchVersion={operationMatch.Version}");
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// 手动识别：冻结游戏画面 → 弹窗框选大门/侧门 → 手动几何排名 →
    /// 若有歧义弹候选窗口供玩家选择 → 应用结果到 Overlay。
    /// 该链路恢复自旧 MapRuntimeService.ManualRecognition.cs 的完整交互。
    /// </summary>
    private async Task RunManualRecognitionCoreAsync(
        MapMatchSnapshot operationMatch,
        CancellationToken cancellationToken)
    {
        // 冻结画面：捕获整个客户区，让玩家在拖框窗口内框选双门
        if (!_captureSvc.TryCaptureClient(out var frameObj, out _)
            || frameObj is not CapturedGameFrame frame)
        {
            _statusMessage = "手动识别截图失败，请保持游戏在前台并打开地图。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        using (frame)
        {
            var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(
                frame.ClientBounds,
                _settings!.ResolveMapViewportRegion(
                    (int)Math.Round(frame.ClientBounds.Width),
                    (int)Math.Round(frame.ClientBounds.Height))
                    ?? new NormalizedRectangle { X = 0, Y = 0, Width = 1, Height = 1 });
            if (!viewportBounds.IsValid)
            {
                _statusMessage = "已校准的地图区域无效，请重新校准。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _statusMessage = "手动识别中……请框选大门和侧门。";
            StateChanged?.Invoke(this, EventArgs.Empty);

            ManualGateSelectionResult? selection;
            _manualSelectionActive = true;
            try
            {
                selection = await MapManualRecognitionWindow.ShowAsync(
                    frame,
                    viewportBounds,
                    cancellationToken);
            }
            finally
            {
                _manualSelectionActive = false;
            }

            if (selection is null)
            {
                _statusMessage = "已取消手动识别。";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            var attempt = await Task.Run(
                () => _recognition.RecognizeManual(
                    viewportBounds,
                    selection.MainGateBounds,
                    selection.SideGateBounds,
                    _settings.OverlayAlignmentMode,
                    _settings.RecognitionTuning.Clone(),
                    mapClass: operationMatch.MapClass));
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
                return;

            _lastDiagnostics = attempt.Diagnostics;

            RuntimeMapRecognition? recognition = attempt.Recognition;
            if (recognition is null && attempt.Choices.Count > 0)
            {
                var selectedIndex = await MapManualCandidateWindow.ShowAsync(
                    frame,
                    attempt.Choices,
                    attempt.FailureReason,
                    cancellationToken);
                if (selectedIndex is null)
                {
                    _statusMessage = "已取消候选确认。";
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
                recognition = MapCvRecognitionService.ConfirmChoice(
                    attempt.Choices[selectedIndex.Value]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentMatchOperation(operationMatch))
                return;

            if (recognition is null)
            {
                _statusMessage = $"手动识别失败：{attempt.FailureReason}";
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            RecordSuccessfulAlignment(recognition, frame);
            await PersistPreprocessedScaleAsync(
                recognition,
                frame,
                attempt.Diagnostics);
            _lastRecognition = recognition;
            _lastAlignmentSession = UpdateAlignmentSession(
                _lastAlignmentSession,
                recognition);
            RememberPrimaryFloorSession(recognition, _lastAlignmentSession);
            _lastGameBounds = frame.ClientBounds;
            _lastGameWindowHandle = frame.WindowHandle;
            _statusMessage =
                $"手动识别：{recognition.Map.DisplayName} · {recognition.Result.Floor.ToUpperInvariant()}";
            _logCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Info,
                $"手动识别完成 · map={recognition.Map.Id} · floor={recognition.Result.Floor}",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor,
                    ["confidence"] = recognition.Result.Confidence
                });
            _overlay.UpdateMap(
                recognition,
                frame.ClientBounds,
                frame.WindowHandle,
                _settings.ShowOverlayStatus);
            ShowTransientAlignmentSuccess(
                recognition,
                frame.ClientBounds,
                frame.WindowHandle,
                attempt.Diagnostics);
            _overlay.Show();
            RefreshMiniMapForCurrentFloor();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public void ToggleOverlay()
    {
        if (_disposed || _settings is null || !_settings.IsEnabled)
            return;

        if (_overlay.HasMap || _overlay.IsVisible)
        {
            _overlay.Toggle();
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Info,
                $"Overlay 已切换 · visible={_overlay.IsVisible} · hasMap={_overlay.HasMap}");
            return;
        }

        if (!_captureSvc.TryGetForegroundClientBounds(
                out var clientBoundsObj,
                out var windowHandle,
                out var failureReason)
            || clientBoundsObj is not MapScreenRect gameBounds)
        {
            _statusMessage = $"Overlay 无法显示：{failureReason}";
            _logCollector.Append(
                MapLogCategory.Overlay,
                MapLogLevel.Warning,
                _statusMessage);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        const string title = "Overlay 已响应 F5";
        const string message = "当前尚未加载地图。请打开游戏地图后再按地图键，或等待识别完成。";
        _overlayStatus.Show(
            new MapOverlayStatus(
                MapOverlayStatusLevel.Warning,
                title,
                message,
                $"窗口句柄 0x{windowHandle.ToInt64():X}"),
            gameBounds,
            windowHandle,
            showStatusPreference: true,
            transient: true);
        _overlay.Show();
        _logCollector.Append(
            MapLogCategory.Overlay,
            MapLogLevel.Info,
            $"F5 已响应，但当前没有地图内容 · visible={_overlay.IsVisible}",
            details: new()
            {
                ["hasMap"] = _overlay.HasMap,
                ["windowHandle"] = $"0x{windowHandle.ToInt64():X}",
                ["bounds"] = $"{gameBounds.X:F0},{gameBounds.Y:F0},{gameBounds.Width:F0}x{gameBounds.Height:F0}"
            });
    }

    public void ToggleControlPanel()
    {
        if (_disposed || !_settings!.IsEnabled || _controlPanel is null) return;
        if (_controlPanel.IsVisible)
        {
            _controlPanel.Hide();
            _statusMessage = "外置控件层已隐藏。";
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_manualSelectionActive) return;
        _ = ToggleControlPanelAsync();
    }

    private async Task ToggleControlPanelAsync()
    {
        if (_controlPanel is null) return;
        try
        {
            if (!_captureSvc.TryGetForegroundClientBounds(
                out var clientBoundsObj, out var hwnd, out _))
                return;
            if (clientBoundsObj is not MapScreenRect gameBounds)
                return;
            await _controlPanel.ShowAsync(gameBounds, hwnd, _matchSession.Snapshot);
        }
        catch { /* 控制面板显示失败不阻塞 */ }
    }

    public bool TryCaptureCalibrationFrame(
        out CapturedGameFrame? frame, out string failureReason)
    {
        if (_captureSvc.TryCaptureClient(out var frameObj, out failureReason))
        {
            frame = frameObj as CapturedGameFrame;
            return frame != null;
        }
        frame = null;
        return false;
    }

    public bool TryRestartElevated(out string failureReason) =>
        GameProcessIntegrityService.TryRestartElevated(out failureReason);

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void CheckIntegrityAndNotify()
    {
        IntegrityStatus = GameProcessIntegrityService.Check();
        if (IntegrityStatus.RequiresElevation && !_elevationEventRaised)
        {
            _elevationEventRaised = true;
            ElevationRequiredDetected?.Invoke(this, EventArgs.Empty);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ════════════════ Dispose ════════════════

    public void Dispose() { _ = DisposeAsync(); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _matchLifecycleGate.WaitAsync();
        try
        {
            // Stop producing new match-scoped work before draining anything
            // already queued. Keep the lifecycle gate alive after disposal so
            // a cache hotkey callback queued just before ClearBindings can
            // acquire it, observe the ended match, and return safely.
            _input.ClearBindings();
            Volatile.Write(ref _matchEnding, 1);
            if (_matchSession.Snapshot.IsStarted)
                _matchSession.End();
            _lifetimeCts.Cancel();
            CancelMatchOperations();
            await DrainMatchOperationsAsync();
            await DrainMapCacheWritesAsync();
            ResetMatchTransientState(resetAutomaticCacheSamples: true);
        }
        finally
        {
            _matchLifecycleGate.Release();
        }
        _input.Dispose();
        _overlayStatus.Dispose();
        _overlay.Dispose();
        _gateDetector.Dispose();
        _floorRecognizer.Dispose();
        _playerMarkerSvc.Dispose();
        _recognition.Dispose();
        _playerMarkerDetector.Dispose();
        _controlPanel?.Dispose();
        await _researchCollector.DisposeAsync();
        await _logCollector.DisposeAsync();
        _initializeGate.Dispose();
        _scanGate.Dispose();
        _matchCancellation?.Dispose();
        _mapCacheWriteGate.Dispose();
        _lifetimeCts.Dispose();
        MapLogCollector.Instance = null!;
        StateChanged = null;
        ElevationRequiredDetected = null;
    }
}
