using Microsoft.UI.Dispatching;
using OpenCvSharp;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Phase 1: stability result that owns the last two stable frames.
/// Dispose after consumption; failure / cancellation paths must dispose all retained frames.
/// </summary>
public sealed class StableViewportCaptureResult : IDisposable
{
    /// <summary>
    /// The frame retained immediately before the frame that satisfied the
    /// stability check. This is the historical primary alignment input.
    /// </summary>
    public CapturedGameFrame? PrimaryFrame { get; init; }

    /// <summary>
    /// The newest frame, i.e. the frame that caused the stability check to
    /// pass. It is newer than <see cref="PrimaryFrame"/> by one capture.
    /// </summary>
    public CapturedGameFrame? ConfirmationFrame { get; init; }
    public int Attempts { get; init; }
    public int SuccessfulCaptures { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public double CaptureMilliseconds { get; init; }
    public double DelayMilliseconds { get; init; }

    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        PrimaryFrame?.Dispose();
        ConfirmationFrame?.Dispose();
    }
}

/// <summary>Owns persisted status, pass-through bindings, recognition, and overlay state.</summary>
public sealed class MapRuntimeService : IDisposable, IAsyncDisposable
{
    private sealed record FloorScaleSeed(
        MapOverlayTransform Transform,
        double PrimaryScale,
        bool IsCalibrated,
        string Source);

    private readonly DispatcherQueue _dispatcher;
    private readonly MapRuntimeSettingsRepository _settingsRepository = new();
    private readonly MapRepository _mapRepository = new();
    private readonly DwrGameWindowCaptureService _captureService = new();
    private readonly MapOverlayWindow _overlay = new();
    private readonly MapControlPanelWindow _controlPanel;
    private readonly MapGlobalInputService _input;
    private readonly MapCvRecognitionService _recognition;
    private readonly MapFloorRecognitionWorker _floorRecognition;
    private readonly MapPlayerMarkerDetector _playerMarkerDetector = new();
    private readonly MapOpenSession _mapOpenSession = new();
    private readonly MapCandidateStabilityTracker _candidateStability = new();
    private readonly MapAlignmentCommitGuard _alignmentCommitGuard = new();
    private readonly MapAlignmentResearchCollector _researchCollector = new();
    private readonly MapRecognitionStatisticsRepository _recognitionStatisticsRepository = new();
    private readonly object _sessionStateGate = new();
    private readonly object _recognitionStatisticsGate = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _activeOperationsGate = new();
    private readonly HashSet<Task> _activeOperations = [];
    private readonly object _apiOperationsGate = new();
    private readonly object _disposeGate = new();
    private readonly object _explicitRecognitionGate = new();
    private readonly MapGameToggleState _gameMapToggleState = new();
    private readonly MapMatchSession _matchSession = new();
    private readonly MapMatchMapLease _selectedMapLease = new();
    private Timer? _integrityMonitor;
    private CancellationTokenSource? _gameMapRefreshCancellation;
    private CancellationTokenSource? _matchCancellation;
    private CancellationTokenSource? _sessionMonitorCancellation;
    private CancellationTokenSource? _explicitRecognitionCancellation;
    private int _explicitRecognitionPriority = -1;
    private Task? _sessionMonitorTask;
    private MapAlignmentSession? _alignmentSession;
    private MapAlignmentTrackingMode _alignmentTrackingMode = MapAlignmentTrackingMode.None;
    private MapScreenRect _lastGameBounds;
    private string? _manualFloorOverrideKey;
    private IntPtr _lastGameWindowHandle;
    private MapWindowSignature? _lockedWindowSignature;
    private MapReferencePoint? _lastTrustedPlayerPoint;
    private DateTimeOffset _lastPlayerObservedAt;
    private MapOverlayStatus _currentOverlayStatus = new(
        MapOverlayStatusLevel.Ready,
        "解锁地图状态",
        "解锁地图尚未启动。");
    private bool _initialized;
    private volatile bool _disposed;
    private bool _elevationEventRaised;
    private bool _manualSelectionActive;
    private int _apiOperationCount;
    private TaskCompletionSource<bool>? _apiOperationsDrained;
    private Task? _disposeTask;
    private Task _recognitionStatisticsWriteTask = Task.CompletedTask;
    private bool _recognitionAttemptStarted;
    private bool _recognitionAttemptProducedAlignment;

    public MapRuntimeService(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _input = new MapGlobalInputService(dispatcher);
        _recognition = new MapCvRecognitionService(_mapRepository);
        _floorRecognition = new MapFloorRecognitionWorker(
            ResolveFloorReferencePath("1F.png"),
            ResolveFloorReferencePath("2F.png"));
        _controlPanel = new MapControlPanelWindow(
            (playerSlot, mapClass) => StartTrackedOperation(
                _ => BeginMatchAsync(playerSlot, mapClass)),
            GetMapClassesAsync,
            () => StartTrackedOperation(_ => EndMatchAsync()));
        _input.QuickScanInvoked += (_, args) =>
        {
            if (_matchSession.Snapshot.IsStarted)
                _ = StartTrackedOperation(
                    cancellationToken => RunQuickScanForCurrentMatchAsync(
                        args.Timestamp,
                        cancellationToken));
        };
        _input.OverlayToggleInvoked += (_, _) => ToggleOverlay();
        _input.ManualRecognitionInvoked += (_, args) =>
        {
            if (_matchSession.Snapshot.IsStarted)
                _ = StartTrackedOperation(
                    cancellationToken =>
                        RunManualRecognitionForCurrentMatchAsync(
                            args.Timestamp,
                            cancellationToken));
        };
        _input.GameMapToggleInvoked += (_, args) =>
            _ = StartTrackedOperation(
                _ => HandleGameMapToggleAsync(args.Timestamp));
        _input.ControlPanelToggleInvoked += (_, _) =>
            ToggleControlPanel();
        _input.SwitchFloorInvoked += (_, _) =>
            HandleSwitchFloor();
    }

    public MapRuntimeSettings Settings { get; private set; } = new();
    public MapRecord? SelectedMap { get; private set; }
    /// <summary>V6: floor key currently active (e.g. "1f", "2f").</summary>
    public string? CurrentFloorKey { get; private set; }
    public MapFloorRecognitionResult? LastFloorRecognition { get; private set; }
    public MapAlignmentTrackingMode AlignmentTrackingMode => _alignmentTrackingMode;
    public MapSessionSnapshot SessionSnapshot
    {
        get
        {
            lock (_sessionStateGate)
                return _mapOpenSession.Snapshot;
        }
    }
    public MapReferencePoint? LastTrustedPlayerPosition =>
        _lastTrustedPlayerPoint;
    public MapMatchSnapshot MatchSnapshot => _matchSession.Snapshot;
    public bool ArePlayerAssetsReady => MapPlayerAssetCatalog.AreAllAvailable;
    public RuntimeMapRecognition? LastRecognition { get; private set; }
    public MapScanDiagnostics? LastDiagnostics { get; private set; }
    public GameIntegrityStatus IntegrityStatus { get; private set; } =
        new(false, false, false, false, "尚未检查游戏权限。");
    public string StatusMessage { get; private set; } = "解锁地图尚未启动。";
    public bool IsScanning { get; private set; }
    public bool IsOverlayVisible => _overlay.IsVisible;
    public bool IsControlPanelVisible => _controlPanel.IsVisible;
    public int ReadyMapCount => _recognition.ReadyMapCount;
    public int TotalMapCount => _recognition.TotalMapCount;
    public MapLogCollector LogCollector { get; } = new();
    public MapAlignmentResearchCollector ResearchCollector => _researchCollector;
    public event EventHandler? StateChanged;
    public event EventHandler? ElevationRequiredDetected;

    public async Task InitializeAsync()
    {
        using var operation = EnterApiOperation();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
            return;
        await _initializeGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized)
                return;
            Settings = await _settingsRepository.LoadAsync();
            ApplyOverlayDisplaySettings(Settings);
            _ = RepairMapMetadataInBackgroundAsync();
            await _recognition.RefreshCacheAsync();
            await RefreshSelectedMapReferenceAsync();
            if (Settings.IsEnabled)
            {
                try
                {
                    ApplyInputBindings(Settings);
                    StatusMessage = BuildReadyStatus();
                    SetCurrentOverlayStatus(
                        MapOverlayStatusLevel.Ready,
                        "解锁地图状态",
                        StatusMessage);
                }
                catch (Exception exception)
                {
                    Settings.IsEnabled = false;
                    await _settingsRepository.SaveAsync(Settings);
                    StatusMessage = $"快捷键监听失败，解锁地图未启动：{exception.Message}";
                }
            }
            _initialized = true;
            CheckIntegrityAndNotify();
            if (Settings.CollectLogs && !LogCollector.IsEnabled)
                LogCollector.IsEnabled = true;
            if (Settings.CollectAlignmentResearchData)
            {
                try
                {
                    await _researchCollector.SetEnabledAsync(true);
                }
                catch (Exception exception)
                {
                    LogCollector.Append(
                        MapLogCategory.System,
                        MapLogLevel.Warning,
                        $"Alignment research collection could not start: {exception.Message}");
                }
            }
            if (!_disposed)
            {
                _integrityMonitor = new Timer(
                    _ => _dispatcher.TryEnqueue(CheckIntegrityAndNotify),
                    null,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromSeconds(2));
            }
            NotifyStateChanged();
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private void ApplyOverlayDisplaySettings(MapRuntimeSettings settings)
    {
        _overlay.SetShowGateMarkers(settings.ShowGateMarkers);
        _overlay.SetShowAuxiliaryAnchors(settings.ShowAuxiliaryAnchors);
        _overlay.SetShowTextAnnotations(settings.ShowTextAnnotations);
        _overlay.SetShowBoxAnnotations(settings.ShowBoxAnnotations);
        _overlay.SetShowGateMarkersOnMiniMap(settings.ShowGateMarkersOnMiniMap);
        _overlay.SetShowAuxiliaryAnchorsOnMiniMap(settings.ShowAuxiliaryAnchorsOnMiniMap);
        _overlay.SetShowTextAnnotationsOnMiniMap(settings.ShowTextAnnotationsOnMiniMap);
        _overlay.SetShowBoxAnnotationsOnMiniMap(settings.ShowBoxAnnotationsOnMiniMap);
        _overlay.SetShowFloorOnMiniMap(settings.ShowFloorOnMiniMap);
        _overlay.SetStatusOpacity(settings.StatusOpacity);
        _overlay.SetStatusOffsetX(settings.StatusOffsetX);
        _overlay.SetStatusOffsetY(settings.StatusOffsetY);
        _overlay.SetMiniMapOpacity(settings.MiniMapOpacity);
        _overlay.SetMiniMapOffsetX(settings.MiniMapOffsetX);
        _overlay.SetMiniMapOffsetY(settings.MiniMapOffsetY);
        _overlay.SetAllowExtend(settings.AllowMapExtendBeyondBounds);
        _overlay.SetStatusVisible(settings.ShowOverlayStatus);
        _overlay.SetReverseAlternateDisplay(settings.ReverseAlternateDisplay);
    }

    public async Task RefreshMapCacheAsync(Guid? changedMapId = null)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        await _scanGate.WaitAsync(_lifetimeCancellation.Token);
        try
        {
            var previousUpdatedAt = SelectedMap?.UpdatedAt;
            await _recognition.RefreshCacheAsync(changedMapId);
            await RefreshSelectedMapReferenceAsync();
            if (SelectedMap is { } selected
                && previousUpdatedAt is { } previous
                && selected.UpdatedAt != previous)
            {
                InvalidateAlignment(MapAlignmentTrackingMode.Lost);
            }
            if (!IsScanning
                && _currentOverlayStatus.Level == MapOverlayStatusLevel.Ready)
            {
                StatusMessage = Settings.IsEnabled
                    ? BuildReadyStatus()
                    : StatusMessage;
                if (Settings.IsEnabled)
                {
                    SetCurrentOverlayStatus(
                        MapOverlayStatusLevel.Ready,
                        "解锁地图状态",
                        StatusMessage);
                }
            }
            NotifyStateChanged();
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.IsEnabled == enabled)
            return;
        if (enabled)
        {
            ResetGameMapToggleState();
            ApplyInputBindings(Settings);
            Settings.IsEnabled = true;
            CheckIntegrityAndNotify();
            StatusMessage = IntegrityStatus.RequiresElevation
                ? IntegrityStatus.Message
                : BuildReadyStatus();
            SetCurrentOverlayStatus(
                IntegrityStatus.RequiresElevation
                    ? MapOverlayStatusLevel.Warning
                    : MapOverlayStatusLevel.Ready,
                IntegrityStatus.RequiresElevation
                    ? "游戏内热键不可用"
                    : "解锁地图状态",
                StatusMessage);
        }
        else
        {
            Settings.IsEnabled = false;
            await StopSessionMonitorAsync();
            CancelMatchWork();
            var endedMatch = _matchSession.End();
            _controlPanel.Reset(endedMatch);
            _controlPanel.Hide(restoreGameFocus: false);
            ResetGameMapToggleState();
            _input.ClearBindings();
            _overlay.Clear();
            _lastTrustedPlayerPoint = null;
            _lastPlayerObservedAt = DateTimeOffset.MinValue;
            CurrentFloorKey = null;
            LastFloorRecognition = null;
            InvalidateAlignment(
                SelectedMap is null
                    ? MapAlignmentTrackingMode.None
                    : MapAlignmentTrackingMode.Lost);
            ClearMatchScopedMapState();
            CloseMapSession("解锁地图已关闭。");
            StatusMessage = SelectedMap is null
                ? "解锁地图已关闭。"
                : $"解锁地图已关闭；已保留 {SelectedMap.DisplayName} 的选择。";
        }
        await _settingsRepository.SaveAsync(Settings);
        NotifyStateChanged();
    }

    public async Task BeginMatchAsync(
        PlayerSlot playerSlot,
        string mapClass = "S1")
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (!Settings.IsEnabled)
            throw new InvalidOperationException("请先启用解锁地图功能。");
        if (!ArePlayerAssetsReady)
            throw new InvalidOperationException("四张玩家序号图片不完整，无法开始对局。");
        if (IsScanning)
            throw new InvalidOperationException("当前识别尚未结束，请稍后再开始对局。");
        if (_matchSession.Snapshot.IsStarted)
            throw new InvalidOperationException("当前对局已经开始。");

        var catalog = await _mapRepository.GetCatalogSnapshotAsync();
        var selectedClass = catalog.Classes.FirstOrDefault(candidate =>
            string.Equals(candidate, mapClass, StringComparison.OrdinalIgnoreCase));
        if (selectedClass is null)
            throw new InvalidOperationException(
                "请选择一个仍存在于地图库中的地图模式。");

        await StopSessionMonitorAsync();
        ResetGameMapToggleState();
        CloseMapSession("新对局开始前已清空旧地图会话。");
        _lastTrustedPlayerPoint = null;
        _lastPlayerObservedAt = DateTimeOffset.MinValue;
        CurrentFloorKey = null;
        LastFloorRecognition = null;
        CancelMatchWork();
        ClearMatchScopedMapState();
        await _settingsRepository.SaveAsync(Settings);
        var snapshot = _matchSession.Begin(playerSlot, selectedClass);
        _matchCancellation = new CancellationTokenSource();
        _controlPanel.Refresh(snapshot);
        StartSessionMonitor();
        StatusMessage = $"对局已开始，正在追踪 {(int)playerSlot} 号玩家。";
        SetCurrentOverlayStatus(
            MapOverlayStatusLevel.Ready,
            "对局已开始",
            StatusMessage);
        NotifyStateChanged();
    }

    public async Task EndMatchAsync()
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        await EndMatchCoreAsync(
            "对局已结束，本局地图和玩家状态已清空。");
    }

    public void ToggleControlPanel() =>
        _ = StartTrackedOperation(_ => ToggleControlPanelAsync());

    private async Task ToggleControlPanelAsync()
    {
        if (_disposed || !Settings.IsEnabled)
            return;
        if (_controlPanel.IsVisible)
        {
            _controlPanel.Hide();
            StatusMessage = "外置控件层已隐藏。";
            NotifyStateChanged();
            return;
        }
        if (_manualSelectionActive)
        {
            StatusMessage = "手动框选进行中，暂时不能打开外置控件层。";
            NotifyStateChanged();
            return;
        }
        if (!ArePlayerAssetsReady)
        {
            StatusMessage = "四张玩家序号图片不完整，无法打开外置控件层。";
            NotifyStateChanged();
            return;
        }
        if (!_captureService.TryGetForegroundClientBounds(
                out var gameBounds,
                out var gameWindowHandle,
                out var failureReason))
        {
            StatusMessage = failureReason;
            NotifyStateChanged();
            return;
        }

        try
        {
            // Transfer input ownership to the control panel like an in-game
            // overlay: release anything the game currently considers held
            // before the new window receives focus.
            _input.ReleaseAllPressedInputs();
            await _controlPanel.ShowAsync(
                gameBounds,
                gameWindowHandle,
                _matchSession.Snapshot);
            StatusMessage = "外置控件层已打开。";
        }
        catch (Exception exception)
        {
            _controlPanel.Hide(restoreGameFocus: false);
            StatusMessage = $"无法打开外置控件层：{exception.Message}";
        }
        NotifyStateChanged();
    }

    private async Task EndMatchCoreAsync(string message)
    {
        var snapshot = _matchSession.End();
        CancelMatchWork();
        await StopSessionMonitorAsync();
        ResetGameMapToggleState();
        CloseMapSession(message);
        ClearMatchScopedMapState();
        await _settingsRepository.SaveAsync(Settings);
        _overlay.ClearPersistentMiniMap();
        LastRecognition = null;
        _lastTrustedPlayerPoint = null;
        _lastPlayerObservedAt = DateTimeOffset.MinValue;
        CurrentFloorKey = null;
        LastFloorRecognition = null;
        _controlPanel.Reset(snapshot);
        StatusMessage = message;
        SetCurrentOverlayStatus(
            MapOverlayStatusLevel.Ready,
            "对局已结束",
            message);
        NotifyStateChanged();
    }

    public async Task SetBindingAsync(
        MapRuntimeBindingTarget target,
        MapInputBinding binding)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (!binding.IsConfigured)
            throw new InvalidOperationException("请选择一个有效的键盘或鼠标按键。");
        if (binding.Kind == MapInputBindingKind.Keyboard && IsModifierKey(binding.VirtualKey))
            throw new InvalidOperationException("修饰键不能单独作为全局快捷键。");

        var proposed = Settings.Clone();
        switch (target)
        {
            case MapRuntimeBindingTarget.QuickScan:
                proposed.QuickScanBinding = binding.Clone();
                break;
            case MapRuntimeBindingTarget.OverlayToggle:
                proposed.OverlayToggleBinding = binding.Clone();
                break;
            case MapRuntimeBindingTarget.ManualRecognition:
                proposed.ManualRecognitionBinding = binding.Clone();
                break;
            case MapRuntimeBindingTarget.GameMapToggle:
                proposed.GameMapToggleBinding = binding.Clone();
                break;
            case MapRuntimeBindingTarget.ControlPanelToggle:
                proposed.ControlPanelToggleBinding = binding.Clone();
                break;
            case MapRuntimeBindingTarget.SwitchFloor:
                proposed.SwitchFloorBinding = binding.Clone();
                break;
            default:
                throw new InvalidOperationException("未知的快捷键用途。");
        }
        EnsureBindingsAreDistinct(proposed);

        if (proposed.IsEnabled)
        {
            var previous = Settings.Clone();
            try
            {
                ApplyInputBindings(proposed);
            }
            catch
            {
                try
                {
                    ApplyInputBindings(previous);
                }
                catch
                {
                    // Preserve the first, more actionable exception.
                }
                throw;
            }
        }
        Settings = proposed;
        if (target == MapRuntimeBindingTarget.GameMapToggle)
            ResetGameMapToggleState();
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = target switch
        {
            MapRuntimeBindingTarget.QuickScan => "已更新快捷扫描绑定。",
            MapRuntimeBindingTarget.OverlayToggle => "已更新识别图层绑定。",
            MapRuntimeBindingTarget.ManualRecognition => "已更新手动识别绑定。",
            MapRuntimeBindingTarget.GameMapToggle => "已更新游戏地图开关绑定。",
            MapRuntimeBindingTarget.SwitchFloor => "已更新楼层切换绑定。",
            _ => "已更新外置控件层绑定。"
        };
        NotifyStateChanged();
    }

    public async Task SetRecognitionTuningAsync(MapRecognitionTuning tuning)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (IsScanning)
            throw new InvalidOperationException("识别进行中，暂时不能修改参数。");
        var proposed = Settings.Clone();
        proposed.RecognitionTuning = tuning?.Clone() ?? new MapRecognitionTuning();
        proposed.RecognitionTuning.Normalize();
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = "识别参数已保存，将从下一次识别开始生效。";
        NotifyStateChanged();
    }

    public async Task RestoreRecognitionTuningDefaultsAsync() =>
        await SetRecognitionTuningAsync(
            MapRuntimeSettings.CreateDefault().RecognitionTuning);

    /// <summary>设置首次扫描策略（双门对齐 / 侧门扫描）并持久化。</summary>
    public async Task SetFirstScanStrategyAsync(FirstScanStrategy strategy)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.FirstScanStrategy == strategy)
            return;
        var proposed = Settings.Clone();
        proposed.FirstScanStrategy = strategy;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = strategy == FirstScanStrategy.SideEntrance
            ? "首次扫描策略已切换为侧门扫描。按下扫描键后将执行侧门特征匹配。"
            : "首次扫描策略已切换为双门对齐（默认）。";
        NotifyStateChanged();
    }

    /// <summary>
    /// 批量重建所有地图的侧门特征图，并刷新识别缓存。
    /// 通常由"侧门特征半径"参数变更时触发。
    /// </summary>
    public async Task RebuildSideEntranceFeaturesAsync(
        IProgress<(int done, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        IsScanning = true;
        StatusMessage = "正在重建侧门特征……";
        NotifyStateChanged();
        try
        {
            await _mapRepository.RebuildAllSideEntranceFeaturesAsync(
                Settings.RecognitionTuning.SideEntranceFeatureRadius,
                progress,
                cancellationToken);
            await _recognition.RefreshCacheAsync();
            StatusMessage = "侧门特征重建完成。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "侧门特征重建已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"侧门特征重建失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
            NotifyStateChanged();
        }
    }

    public async Task SetStructureRegistrationTuningAsync(
        MapStructureRegistrationTuning tuning)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (IsScanning)
            throw new InvalidOperationException("识别进行中，暂时不能修改结构配准参数。");
        var proposed = Settings.Clone();
        proposed.StructureRegistrationTuning =
            tuning?.Clone() ?? new MapStructureRegistrationTuning();
        proposed.StructureRegistrationTuning.Normalize();
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = "结构配准参数已保存，将从下一次开图开始生效。";
        NotifyStateChanged();
    }

    public async Task RestoreStructureRegistrationTuningDefaultsAsync() =>
        await SetStructureRegistrationTuningAsync(
            MapRuntimeSettings.CreateDefault().StructureRegistrationTuning);

    public async Task SetSessionTuningAsync(MapSessionTuning tuning)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (IsScanning)
            throw new InvalidOperationException("识别进行中，暂时不能修改会话参数。");
        var proposed = Settings.Clone();
        proposed.SessionTuning = tuning?.Clone() ?? new MapSessionTuning();
        proposed.SessionTuning.Normalize();
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = "地图解锁稳定性与时序参数已保存。";
        NotifyStateChanged();
    }

    private async Task<IReadOnlyList<string>> GetMapClassesAsync()
    {
        var snapshot = await _mapRepository.GetCatalogSnapshotAsync();
        return snapshot.Classes;
    }

    private async Task RepairMapMetadataInBackgroundAsync()
    {
        try
        {
            await _mapRepository.RepairImageMetadataAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Map metadata repair failed: {exception}");
        }
    }

    public async Task RestoreSessionTuningDefaultsAsync() =>
        await SetSessionTuningAsync(MapRuntimeSettings.CreateDefault().SessionTuning);

    public async Task RestoreFloorRecognitionTuningDefaultsAsync() =>
        await SetFloorRecognitionTuningAsync(
            MapRuntimeSettings.CreateDefault().FloorRecognitionTuning);

    public async Task RestorePlayerTrackingTuningDefaultsAsync() =>
        await SetPlayerTrackingTuningAsync(
            MapRuntimeSettings.CreateDefault().PlayerTrackingTuning);

    public async Task SetFloorRecognitionTuningAsync(
        MapFloorRecognitionTuning tuning)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (IsScanning)
            throw new InvalidOperationException("识别进行中，暂时不能修改楼层识别参数。");
        var proposed = Settings.Clone();
        proposed.FloorRecognitionTuning = tuning?.Clone()
            ?? new MapFloorRecognitionTuning();
        proposed.FloorRecognitionTuning.Normalize();
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = "楼层识别参数已保存。";
        NotifyStateChanged();
    }

    public async Task SetPlayerTrackingTuningAsync(
        MapPlayerTrackingTuning tuning)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (IsScanning)
            throw new InvalidOperationException("识别进行中，暂时不能修改玩家跟踪参数。");
        var proposed = Settings.Clone();
        proposed.PlayerTrackingTuning = tuning?.Clone()
            ?? new MapPlayerTrackingTuning();
        proposed.PlayerTrackingTuning.Normalize();
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = "玩家跟踪参数已保存。";
        NotifyStateChanged();
    }

    public async Task SetOverlayStatusVisibleAsync(bool visible)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowOverlayStatus == visible)
            return;
        var proposed = Settings.Clone();
        proposed.ShowOverlayStatus = visible;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetStatusVisible(visible);
        StatusMessage = visible
            ? "识别图层将同时显示左上角状态。"
            : "有地图结果时仅显示地图；没有地图结果时仍会显示状态。";
        NotifyStateChanged();
    }

    public async Task SetReverseAlternateDisplayAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ReverseAlternateDisplay == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.ReverseAlternateDisplay = enabled;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetReverseAlternateDisplay(enabled);
        StatusMessage = enabled
            ? "反向交替显示已开启：大地图开启时显示状态，关闭时隐藏。"
            : "反向交替显示已关闭：恢复默认交替模式。";
        NotifyStateChanged();
    }

    public async Task SetCollectLogsAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.CollectLogs == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.CollectLogs = enabled;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        LogCollector.IsEnabled = enabled;
        StatusMessage = enabled
            ? "日志收集已开启。"
            : "日志收集已关闭，日志已保存到磁盘。";
        NotifyStateChanged();
    }

    public async Task SetSkipFloorRecognitionAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.SkipFloorRecognition == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.SkipFloorRecognition = enabled;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = enabled
            ? "已跳过楼层识别：全程按 1F 处理，省去 ~110-130ms。"
            : "楼层识别已恢复：将截图并模板匹配判断 1F/2F。";
        NotifyStateChanged();
    }

    public async Task SetSkipStabilityConfirmationAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.SessionTuning.SkipStabilityConfirmation == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.SessionTuning.SkipStabilityConfirmation = enabled;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        StatusMessage = enabled
            ? "已跳过稳定确认：中等置信度结果直接接受，省去连续帧确认等待。"
            : "稳定确认已恢复：中等置信度结果需等待连续帧确认。";
        NotifyStateChanged();
    }

    public async Task SetMediumConfidenceAsync(double confidence)
    {
        await InitializeAsync();
        var tuning = Settings.SessionTuning.Clone();
        tuning.MediumConfidence = confidence;
        await SetSessionTuningAsync(tuning);
    }

    public async Task SetAllowMapExtendBeyondBoundsAsync(bool allow)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.AllowMapExtendBeyondBounds == allow)
            return;
        var proposed = Settings.Clone();
        proposed.AllowMapExtendBeyondBounds = allow;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetAllowExtend(allow);
        StatusMessage = allow
            ? "地图图片允许超出校准区域边界。"
            : "地图图片将裁剪至校准区域内。";
        NotifyStateChanged();
    }

    public async Task SetPlayerTrackingEnabledAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.PlayerTrackingEnabled == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.PlayerTrackingEnabled = enabled;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        if (!enabled)
        {
            _lastTrustedPlayerPoint = null;
            _playerMarkerDetector.ResetTracking();
            lock (_sessionStateGate)
                _mapOpenSession.UpdatePlayer(null);
            _overlay.UpdatePlayer(null);
        }
        StatusMessage = enabled
            ? "玩家位置追踪已开启。"
            : "玩家位置追踪已关闭，已清除当前玩家坐标。";
        NotifyStateChanged();
    }

    public async Task SetShowGateMarkersAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowGateMarkers == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowGateMarkers = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowGateMarkers(show);
        NotifyStateChanged();
    }

    public async Task SetShowAuxiliaryAnchorsAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowAuxiliaryAnchors == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowAuxiliaryAnchors = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowAuxiliaryAnchors(show);
        NotifyStateChanged();
    }

    public async Task SetShowTextAnnotationsAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowTextAnnotations == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowTextAnnotations = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowTextAnnotations(show);
        NotifyStateChanged();
    }

    public async Task SetShowBoxAnnotationsAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowBoxAnnotations == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowBoxAnnotations = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowBoxAnnotations(show);
        NotifyStateChanged();
    }

    public async Task SetShowGateMarkersOnMiniMapAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowGateMarkersOnMiniMap == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowGateMarkersOnMiniMap = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowGateMarkersOnMiniMap(show);
        NotifyStateChanged();
    }

    public async Task SetShowAuxiliaryAnchorsOnMiniMapAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowAuxiliaryAnchorsOnMiniMap == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowAuxiliaryAnchorsOnMiniMap = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowAuxiliaryAnchorsOnMiniMap(show);
        NotifyStateChanged();
    }

    public async Task SetShowTextAnnotationsOnMiniMapAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowTextAnnotationsOnMiniMap == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowTextAnnotationsOnMiniMap = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowTextAnnotationsOnMiniMap(show);
        NotifyStateChanged();
    }

    public async Task SetShowBoxAnnotationsOnMiniMapAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowBoxAnnotationsOnMiniMap == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowBoxAnnotationsOnMiniMap = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowBoxAnnotationsOnMiniMap(show);
        NotifyStateChanged();
    }

    public async Task SetShowFloorOnMiniMapAsync(bool show)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.ShowFloorOnMiniMap == show)
            return;
        var proposed = Settings.Clone();
        proposed.ShowFloorOnMiniMap = show;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetShowFloorOnMiniMap(show);
        NotifyStateChanged();
    }

    public async Task SetStatusOpacityAsync(double opacity)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Math.Abs(Settings.StatusOpacity - opacity) < 0.0001)
            return;
        var proposed = Settings.Clone();
        proposed.StatusOpacity = opacity;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetStatusOpacity(opacity);
        NotifyStateChanged();
    }

    public async Task SetStatusOffsetXAsync(double offsetX)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Math.Abs(Settings.StatusOffsetX - offsetX) < 0.01)
            return;
        var proposed = Settings.Clone();
        proposed.StatusOffsetX = offsetX;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetStatusOffsetX(offsetX);
        NotifyStateChanged();
    }

    public async Task SetStatusOffsetYAsync(double offsetY)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Math.Abs(Settings.StatusOffsetY - offsetY) < 0.01)
            return;
        var proposed = Settings.Clone();
        proposed.StatusOffsetY = offsetY;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetStatusOffsetY(offsetY);
        NotifyStateChanged();
    }

    public async Task SetMiniMapOpacityAsync(double opacity)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Math.Abs(Settings.MiniMapOpacity - opacity) < 0.0001)
            return;
        var proposed = Settings.Clone();
        proposed.MiniMapOpacity = opacity;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetMiniMapOpacity(opacity);
        NotifyStateChanged();
    }

    public async Task SetMiniMapOffsetXAsync(double offsetX)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Math.Abs(Settings.MiniMapOffsetX - offsetX) < 0.01)
            return;
        var proposed = Settings.Clone();
        proposed.MiniMapOffsetX = offsetX;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetMiniMapOffsetX(offsetX);
        NotifyStateChanged();
    }

    public async Task SetMiniMapOffsetYAsync(double offsetY)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Math.Abs(Settings.MiniMapOffsetY - offsetY) < 0.01)
            return;
        var proposed = Settings.Clone();
        proposed.MiniMapOffsetY = offsetY;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        _overlay.SetMiniMapOffsetY(offsetY);
        NotifyStateChanged();
    }

    public async Task SetPersistentMiniMapEnabledAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.PersistentMiniMapEnabled == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.PersistentMiniMapEnabled = enabled;
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        if (!enabled)
            _overlay.ClearPersistentMiniMap();
        else
            TryRestorePersistentMiniMap();
        StatusMessage = enabled
            ? "常驻小地图已开启；识别成功后将始终显示。"
            : "常驻小地图已关闭。";
        NotifyStateChanged();
    }

    public async Task SetMiniMapScaleAsync(double scale)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        var proposed = Settings.Clone();
        proposed.MiniMapScale = scale;
        proposed.Normalize();
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        TryRestorePersistentMiniMap();
        StatusMessage = $"小地图缩放已更新为 {Settings.MiniMapScale:P0}。";
        NotifyStateChanged();
    }

    public async Task SetOverlayAlignmentModeAsync(MapOverlayAlignmentMode mode)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (!Enum.IsDefined(mode))
            throw new InvalidOperationException("请选择有效的图层对齐模式。");
        if (mode != MapOverlayAlignmentMode.Uniform)
            throw new InvalidOperationException("地图会话只支持固定旋转下的等比缩放。");
        if (IsScanning)
            throw new InvalidOperationException("识别进行中，暂时无法切换图层对齐模式。");
        if (Settings.OverlayAlignmentMode == mode)
            return;

        var proposed = Settings.Clone();
        proposed.OverlayAlignmentMode = mode;
        proposed.Normalize();
        await _settingsRepository.SaveAsync(proposed);
        Settings = proposed;
        ClearOverlayMap();
        InvalidateAlignment(
            SelectedMap is null
                ? MapAlignmentTrackingMode.None
                : MapAlignmentTrackingMode.Lost);
        StatusMessage =
            $"已切换为{mode.ToDisplayName()}；地图选择保持不变，"
            + "请在游戏地图打开时完成双门重新对齐。";
        _currentOverlayStatus = new MapOverlayStatus(
            MapOverlayStatusLevel.Ready,
            "需要重新扫描",
            StatusMessage);
        TryPublishOverlayStatus(
            _currentOverlayStatus,
            showImmediately: _overlay.IsVisible);
        NotifyStateChanged();
    }

    public bool TryCaptureCalibrationFrame(
        out CapturedGameFrame? frame,
        out string failureReason) =>
        _captureService.TryCaptureClient(out frame, out failureReason);

    public async Task SetMapViewportAsync(
        NormalizedRectangle region,
        int calibrationClientWidth,
        int calibrationClientHeight)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (!region.IsValid || calibrationClientWidth <= 0 || calibrationClientHeight <= 0)
            throw new InvalidOperationException("地图区域校准结果无效。");
        var proposed = Settings.Clone();
        proposed.MapViewportRegion = region.Clone();
        proposed.CalibrationClientWidth = calibrationClientWidth;
        proposed.CalibrationClientHeight = calibrationClientHeight;
        proposed.CalibrationVersion = MapRuntimeSettings.CurrentCalibrationVersion;
        proposed.Normalize();
        if (!proposed.IsMapViewportCalibrated)
            throw new InvalidOperationException("地图区域校准结果超出游戏客户区。");
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        InvalidateAlignment(
            SelectedMap is null
                ? MapAlignmentTrackingMode.None
                : MapAlignmentTrackingMode.Lost);
        ClearOverlayMap();
        StatusMessage = SelectedMap is null
            ? "地图区域校准已保存。"
            : $"地图区域校准已保存；已保留 {SelectedMap.DisplayName}，需要双门重新对齐。";
        NotifyStateChanged();
    }

    public async Task SetFloorDisplayRegionAsync(
        NormalizedRectangle region,
        int calibrationClientWidth,
        int calibrationClientHeight,
        CapturedGameFrame calibrationFrame)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        ArgumentNullException.ThrowIfNull(calibrationFrame);
        if (!region.IsValid
            || calibrationClientWidth <= 0
            || calibrationClientHeight <= 0)
        {
            throw new InvalidOperationException("楼层显示区校准结果无效。");
        }
        var validation = ValidateFloorDisplayRegion(
            calibrationFrame,
            region);
        if (!validation.Succeeded || validation.Floor is null)
        {
            throw new InvalidOperationException(
                "楼层显示区未保存：当前画面中没有定位到可信的 1/2 数字对，"
                + validation.FailureReason);
        }
        var proposed = Settings.Clone();
        proposed.FloorDisplayRegion = region.Clone();
        proposed.FloorCalibrationClientWidth = calibrationClientWidth;
        proposed.FloorCalibrationClientHeight = calibrationClientHeight;
        proposed.FloorCalibrationVersion =
            MapRuntimeSettings.CurrentCalibrationVersion;
        proposed.Normalize();
        if (!proposed.IsFloorDisplayCalibrated)
            throw new InvalidOperationException("楼层显示区校准结果超出游戏客户区。");
        Settings = proposed;
        await _settingsRepository.SaveAsync(Settings);
        CurrentFloorKey = null;
        LastFloorRecognition = null;
        InvalidateAlignment(
            SelectedMap is null
                ? MapAlignmentTrackingMode.None
                : MapAlignmentTrackingMode.Lost);
        StatusMessage =
            $"楼层显示区校准已验证并保存；当前识别为 "
            + $"{(validation.Floor == "1f" ? "1F" : "2F")}"
            + $"（定位置信度 {validation.LocalizationConfidence:P0}）。";
        NotifyStateChanged();
    }

    private FloorIndicatorClassification ValidateFloorDisplayRegion(
        CapturedGameFrame frame,
        NormalizedRectangle region)
    {
        var left = Math.Clamp(
            (int)Math.Floor(region.X * frame.Image.Width),
            0,
            frame.Image.Width - 1);
        var top = Math.Clamp(
            (int)Math.Floor(region.Y * frame.Image.Height),
            0,
            frame.Image.Height - 1);
        var right = Math.Clamp(
            (int)Math.Ceiling((region.X + region.Width) * frame.Image.Width),
            left + 1,
            frame.Image.Width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((region.Y + region.Height) * frame.Image.Height),
            top + 1,
            frame.Image.Height);
        using var roi = new Mat(
            frame.Image,
            new Rect(left, top, right - left, bottom - top));
        using var recognizer = new FloorIndicatorRecognizer(
            ResolveFloorReferencePath("1F.png"),
            ResolveFloorReferencePath("2F.png"));
        return recognizer.Recognize(roi, Settings.FloorRecognitionTuning);
    }

    private async Task HandleGameMapToggleAsync(long inputTimestamp)
    {
        if (_disposed
            || !Settings.IsEnabled
            || !_matchSession.Snapshot.IsStarted
            || !_captureService.TryGetForegroundClientBounds(
                out _,
                out _,
                out _))
        {
            return;
        }

        CancelPendingGameMapRefresh();
        // A new open event starts a new session even if the previous open
        // attempt ended in LowConfidence/Lost/RecalibrationRequired. Close
        // before toggling so the subsequent transition always starts from
        // Closed and cannot be rejected by the session state machine.
        if (!_gameMapToggleState.IsOpen)
            CloseMapSession("收到地图重新打开操作，已清理旧地图会话。");

        var toggle = _gameMapToggleState.Toggle();
        if (!toggle.IsOpen)
        {
            CloseMapSession("收到地图关闭操作，已废弃本次视口位移。");
            TryRestorePersistentMiniMap();
            try { _overlay.Show(); }
            catch { /* 忽略渲染失败 */ }
            return;
        }
        if (!_gameMapToggleState.TryBeginOpenPipeline(toggle))
            return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            _matchCancellation?.Token ?? CancellationToken.None);
        _gameMapRefreshCancellation = cancellation;
        TransitionSession(
            MapSessionState.OpeningDetected,
            reason: MapRecalibrationReason.MapReopened,
            detail: "收到开图操作，等待原生地图动画。");
        StatusMessage = "检测到游戏地图打开，正在识别楼层……";
        NotifyStateChanged();
        try
        {
            var elapsed = Math.Max(
                0d,
                (Stopwatch.GetTimestamp() - inputTimestamp)
                * 1000d / Stopwatch.Frequency);
            var remainingDelay = Math.Max(
                0,
                Settings.SessionTuning.OpeningAnimationDelayMilliseconds
                - (int)Math.Round(elapsed));
            if (remainingDelay > 0)
            {
                await Task.Delay(
                    remainingDelay,
                    cancellation.Token);
                LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                    $"开图动画等待完成 · {remainingDelay}ms",
                    elapsedMs: remainingDelay);
            }
            if (!_gameMapToggleState.IsCurrent(toggle))
                return;
            TransitionSession(
                MapSessionState.WaitingForStableFrames,
                detail: "正在等待地图视口连续稳定。");
            var stabilityResult = await WaitForStableViewportAsync(
                    cancellation.Token);
            if (stabilityResult.PrimaryFrame is null)
            {
                AbandonOpenAlignment(
                    "Map viewport did not stabilize before the alignment timeout.");
                TransitionSession(
                    MapSessionState.LowConfidence,
                    detail: "开图动画或地图背景在超时前没有稳定。");
                if (!Settings.PersistentMiniMapEnabled)
                    _overlay.Hide();
                StatusMessage = "地图画面未稳定，本次不会复用旧位移。";
                NotifyStateChanged();
                return;
            }
            if (!_gameMapToggleState.IsCurrent(toggle))
                return;
            var preAlignmentTiming = new MapPreAlignmentTiming
            {
                InputToAlignmentStartMs = stabilityResult.ElapsedMilliseconds + remainingDelay,
                AnimationWaitMs = remainingDelay,
                StableViewportWaitMs = stabilityResult.ElapsedMilliseconds,
                StableViewportCaptureMs = stabilityResult.CaptureMilliseconds,
                StableViewportDelayMs = stabilityResult.DelayMilliseconds,
                StableViewportAttempts = stabilityResult.Attempts,
                StableViewportSuccessfulCaptures = stabilityResult.SuccessfulCaptures
            };
            try
            {
                await RunSelectedMapAlignmentAsync(
                    cancellation.Token,
                    inputTimestamp,
                    toggle,
                    stabilityResult,
                    preAlignmentTiming);
            }
            finally
            {
                stabilityResult.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer close/open input superseded this floor/alignment request.
        }
        finally
        {
            if (ReferenceEquals(_gameMapRefreshCancellation, cancellation))
                _gameMapRefreshCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<StableViewportCaptureResult> WaitForStableViewportAsync(
        CancellationToken cancellationToken)
    {
        double captureMs = 0d;
        double delayMs = 0d;
        var attempts = 0;
        var successfulCaptures = 0;
        CapturedGameFrame? previousStableFrame = null;
        CapturedGameFrame? lastStableFrame = null;
        var tuning = Settings.SessionTuning.Clone();
        tuning.Normalize();
        using var stability = new MapViewportStabilityTracker();
        var timeout = Stopwatch.StartNew();
        try
        {
            while (timeout.ElapsedMilliseconds < tuning.OpeningTimeoutMilliseconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                var captureStart = Stopwatch.GetTimestamp();
                if (_captureService.TryCaptureViewport(
                        Settings.MapViewportRegion!,
                        out var frame,
                        out _)
                    && frame is not null)
                {
                    captureMs += (Stopwatch.GetTimestamp() - captureStart)
                        * 1000d / Stopwatch.Frequency;
                    successfulCaptures++;
                    // Take ownership of the frame — do NOT dispose via using.
                    var isStable = stability.Observe(
                        frame.Image,
                        tuning.StableFrameDifference,
                        tuning.StableFrameCount,
                        tuning.ViewportIgnoreRegions);
                    if (isStable)
                    {
                        // Rotate retained frames: keep last two stable frames.
                        previousStableFrame?.Dispose();
                        previousStableFrame = lastStableFrame;
                        lastStableFrame = frame; // ownership transferred
                        var totalMs = timeout.Elapsed.TotalMilliseconds;
                        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                            $"视口稳定检测通过 · {totalMs:F0}ms · 截帧{captureMs:F0}ms · 延迟{delayMs:F0}ms · 共{attempts}次尝试({successfulCaptures}次成功)",
                            elapsedMs: totalMs,
                            details: new()
                            {
                                ["captureMs"] = captureMs,
                                ["delayMs"] = delayMs,
                                ["attempts"] = attempts,
                                ["successful"] = successfulCaptures
                            });
                        return new StableViewportCaptureResult
                        {
                            PrimaryFrame = previousStableFrame,
                            ConfirmationFrame = lastStableFrame,
                            Attempts = attempts,
                            SuccessfulCaptures = successfulCaptures,
                            ElapsedMilliseconds = totalMs,
                            CaptureMilliseconds = captureMs,
                            DelayMilliseconds = delayMs
                        };
                    }
                    // Frame was observed but not the final stable one;
                    // rotate the retention slot.
                    previousStableFrame?.Dispose();
                    previousStableFrame = lastStableFrame;
                    lastStableFrame = frame; // ownership transferred
                }
                var delayStart = Stopwatch.GetTimestamp();
                await Task.Delay(
                    tuning.StableFrameIntervalMilliseconds,
                    cancellationToken);
                delayMs += (Stopwatch.GetTimestamp() - delayStart)
                    * 1000d / Stopwatch.Frequency;
            }
        }
        catch
        {
            // Disposal of retained frames on any failure.
            previousStableFrame?.Dispose();
            lastStableFrame?.Dispose();
            throw;
        }
        // Timeout: dispose retained frames and return failure.
        previousStableFrame?.Dispose();
        lastStableFrame?.Dispose();
        var totalWaitMs = timeout.Elapsed.TotalMilliseconds;
        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
            $"视口稳定检测超时 · {totalWaitMs:F0}ms · 截帧{captureMs:F0}ms · 共{attempts}次尝试({successfulCaptures}次截帧)",
            elapsedMs: totalWaitMs);
        return new StableViewportCaptureResult
        {
            Attempts = attempts,
            SuccessfulCaptures = successfulCaptures,
            ElapsedMilliseconds = totalWaitMs,
            CaptureMilliseconds = captureMs,
            DelayMilliseconds = delayMs
        };
    }

    private bool IsOpenPipelineCurrent(
        MapGameToggleTransition transition) =>
        MapSessionRules.CanContinueOpenPipeline(
            _gameMapToggleState,
            transition,
            SessionSnapshot.State);

    public Task RunQuickScanAsync() =>
        StartTrackedOperation(
            cancellationToken => RunQuickScanForCurrentMatchAsync(
                Stopwatch.GetTimestamp(),
                cancellationToken));

    private async Task RunQuickScanForCurrentMatchAsync(
        long inputTimestamp,
        CancellationToken lifetimeToken)
    {
        var cancellation = BeginExplicitRecognition(
            lifetimeToken,
            MapFloorRecognitionIntent.QuickScan);
        if (cancellation is null)
            return;
        try
        {
            await RunQuickScanAsync(
                cancellation.Token,
                inputTimestamp);
        }
        finally
        {
            CompleteExplicitRecognition(cancellation);
        }
    }

    /// <summary>
    /// 侧门扫描核心逻辑：对已捕获帧运行特征模板匹配，展示 top-5 候选供用户确认。
    /// 确认后将地图锁定并进入正常对齐流程（复用双门管线的后半段）。
    /// </summary>
    private async Task RunSideEntranceScanWithFrameAsync(
        CapturedGameFrame frame,
        MapMatchSnapshot match,
        MapScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        // 1. 运行侧门模板匹配
        PublishStatus(
            "侧门策略：正在进行特征匹配，请稍候……",
            new MapOverlayStatus(
                MapOverlayStatusLevel.Scanning,
                "正在识别地图",
                "侧门策略：正在进行特征匹配……"),
            showOverlay: true);

        var candidates = await Task.Run(
            () => _recognition.RunSideEntranceScan(frame.Image),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (candidates.Count == 0)
        {
            LogCollector.Append(
                MapLogCategory.ScanLifecycle,
                MapLogLevel.Warning,
                "侧门扫描：没有任何地图拥有预处理后的侧门特征图。请先在编辑页面标注侧门锚点并保存。");
            PublishFailure(
                "没有可用的侧门特征图。请在地图编辑页面标注侧门并保存后重试。",
                showOverlay: true);
            return;
        }

        // 2. 将 SideEntranceScanCandidate 转为 MapRecognitionChoice 以复用候选 UI
        var choices = new List<MapRecognitionChoice>(candidates.Count);
        foreach (var candidate in candidates)
        {
            // 候选的楼层键来自侧门特征缓存，可能是任意楼层；识别图必须取对应
            // 楼层的路径，否则候选预览与后续对齐都会用错楼层的图像。
            var recognitionPath = _mapRepository.GetFloorRecognitionPath(
                candidate.Map,
                candidate.FloorKey);
            var result = new MapRecognitionResult
            {
                MapId = candidate.Map.Id,
                Floor = candidate.FloorKey,
                Confidence = candidate.MatchScore,
                Source = MapRecognitionSource.Automatic
            };
            choices.Add(new MapRecognitionChoice
            {
                Recognition = new RuntimeMapRecognition
                {
                    Map = candidate.Map,
                    Result = result,
                    FloorImagePath = recognitionPath
                },
                VectorError = 0d
            });
        }

        // 3. 展示候选选择 UI（最多 5 个）
        if (!Settings.PersistentMiniMapEnabled)
            _overlay.Hide();

        var selectedIndex = await MapManualCandidateWindow.ShowAsync(
            frame,
            choices,
            "侧门扫描完成，请确认当前地图：",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (selectedIndex is null)
        {
            PublishFailure("侧门扫描已取消，未选择地图。", showOverlay: true);
            return;
        }

        // 4. 用户确认后：用侧门特征的位置建立一次性对齐种子，
        //    然后立刻进入单门/特征或无门结构对齐。侧门扫描不是只
        //    确定地图身份后再等待双门的第二阶段。
        var confirmed = choices[selectedIndex.Value].Recognition;
        var confirmedCandidate = candidates[selectedIndex.Value];
        var confirmedSelection = new SideEntranceMapSelection(
            confirmed.Map.Id,
            confirmed.Result.Floor);
        if (!confirmedSelection.Matches(confirmedCandidate)
            || !confirmedSelection.Matches(
                confirmed.Map.Id,
                confirmed.Result.MapId,
                confirmed.Result.Floor))
        {
            PublishFailure(
                "侧门候选的地图身份不一致，已停止对齐以避免使用错误地图。",
                showOverlay: true);
            return;
        }
        if (!_matchSession.IsCurrent(match))
            return;

        LogCollector.Append(
            MapLogCategory.ScanLifecycle,
            MapLogLevel.Info,
            $"侧门扫描：用户选定地图 {confirmed.Map.SequenceNumber} · 得分 {candidates[selectedIndex.Value].MatchScore:P1}");

        CurrentFloorKey = confirmed.Result.Floor;
        // 此时对齐尚未执行，持久小地图会在 ApplySelectedRecognitionAsync
        // → ApplyRecognitionAsync 内部以正确的变换矩阵刷新，此处无需提前调用。
        diagnostics.DetectedFloor = CurrentFloorKey;

        var tuning = Settings.RecognitionTuning.Clone();
        var structureTuning = Settings.StructureRegistrationTuning.Clone();
        var alignmentPrimaryFloorKey = MapFloorRules.GetPrimaryFloorKey(confirmed.Map);
        var usesGateAlignment = string.Equals(
            CurrentFloorKey,
            alignmentPrimaryFloorKey,
            StringComparison.Ordinal);
        PublishStatus(
            $"已识别地图：{confirmed.Map.DisplayName}，正在使用侧门特征直接对齐……",
            new MapOverlayStatus(
                MapOverlayStatusLevel.Scanning,
                confirmed.Map.DisplayName,
                usesGateAlignment
                    ? "正在尝试单门+特征对齐……"
                    : "正在使用无门结构对齐……"),
            showOverlay: true);

        // 侧门扫描的快速通道绕过了常规管道（OpenedDetected
        // → WaitingForStableFrames → IdentifyingMap），必须补齐状态机入口。
        // Closed → CoarseLocating 不是合法转换，因此从 Closed 快进到
        // IdentifyingMap 后再进入 CoarseLocating。已在 LowConfidence 或
        // IdentifyingMap 等允许直接进入 CoarseLocating 的状态则无需额外步骤。
        if (SessionSnapshot.State == MapSessionState.Closed)
        {
            TransitionSession(
                MapSessionState.OpeningDetected,
                mapId: confirmed.Map.Id,
                floor: CurrentFloorKey,
                detail: "侧门扫描快速通道：跳过常规开图检测。");
            TransitionSession(
                MapSessionState.WaitingForStableFrames,
                mapId: confirmed.Map.Id,
                floor: CurrentFloorKey);
            TransitionSession(
                MapSessionState.IdentifyingMap,
                mapId: confirmed.Map.Id,
                floor: CurrentFloorKey);
        }

        TransitionSession(
            MapSessionState.CoarseLocating,
            mapId: confirmed.Map.Id,
            floor: CurrentFloorKey,
            detail: usesGateAlignment
                ? "侧门扫描已识别地图，使用侧门特征种子进入单门+特征对齐。"
                : "侧门扫描已识别地图，使用侧门特征种子进入无门结构对齐。");

        // The candidate window is an explicit user confirmation boundary.
        // Structure registration may validate/refine that exact map, but a
        // different candidate must never replace the map the user selected.
        if (!SideEntranceScanPipeline.TryCreateAlignmentSeed(
                confirmedCandidate,
                frame.ViewportBounds,
                out var sideSeed,
                out var seedFailure))
        {
            TransitionSession(
                MapSessionState.LowConfidence,
                mapId: confirmed.Map.Id,
                floor: CurrentFloorKey,
                confidence: confirmed.Result.Confidence,
                detail: seedFailure);
            PublishFailure(
                $"所选地图 {confirmed.Map.DisplayName} 无法建立侧门对齐种子：{seedFailure}",
                showOverlay: true);
            return;
        }

        if (!confirmedSelection.Matches(sideSeed))
        {
            PublishFailure(
                "侧门对齐种子与用户选择的地图不一致，已停止对齐。",
                showOverlay: true);
            return;
        }

        // Commit the user's map choice before geometric validation. A weak
        // frame may fail to lock, but subsequent attempts in this match must
        // still target the map the user explicitly selected.
        var mapChanged = SelectedMap?.Id != confirmed.Map.Id;
        var proposed = Settings.Clone();
        proposed.SelectedMapId = confirmed.Map.Id;
        await _settingsRepository.SaveAsync(proposed);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_matchSession.IsCurrent(match))
            return;
        Settings = proposed;
        SelectedMap = confirmed.Map.Clone();
        _selectedMapLease.Bind(match, confirmed.Map.Id);
        if (mapChanged)
            _manualFloorOverrideKey = null;
        _alignmentSession = sideSeed;

        AlignmentSearchContext? searchContext = null;
        if (usesGateAlignment && sideSeed.BaselineGateScale > 0d)
        {
            searchContext = new AlignmentSearchContext
            {
                GateSearch = new GateSearchContext
                {
                    Mode = GateSearchMode.WarmScaleSearch,
                    WarmScale = sideSeed.BaselineGateScale,
                    AllowSingleGateEarlyExit = true,
                    SingleGateScoreThreshold =
                        GateTemplateRules.EarlyExitScoreThreshold,
                    SingleGateScaleTolerance =
                        GateTemplateRules.SingleGateScaleTolerance,
                    AmbiguityScoreGap =
                        GateTemplateRules.SingleGateAmbiguityGap,
                }
            };
            if (tuning.WarmGateSearchBudgetMs > 0)
                searchContext.GateSearch.TimeBudgetMilliseconds =
                    tuning.WarmGateSearchBudgetMs;
        }

        var attempt = await Task.Run(
            () => usesGateAlignment
                ? _recognition.AlignSelected(
                    frame,
                    confirmed.Map.Id,
                    sideSeed,
                    MapOverlayAlignmentMode.Uniform,
                    tuning,
                    structureTuning,
                    _lastTrustedPlayerPoint,
                    predictedViewportOrigin: null,
                    BuildLiveIgnoreRegions(frame),
                    alignmentSearchContext: searchContext,
                    nativeScaleChangeRatio:
                        Settings.SessionTuning.NativeScaleChangeRatio,
                    mapClass: _matchSession.Snapshot.MapClass)
                : _recognition.AlignFloorWithoutGates(
                    frame,
                    confirmed.Map.Id,
                    confirmed.Result.Floor,
                    sideSeed.LockedTransform,
                    MapOverlayAlignmentMode.Uniform,
                    tuning,
                    structureTuning,
                    playerPrior: null,
                    predictedViewportOrigin: null,
                    BuildLiveIgnoreRegions(frame)),
            cancellationToken);
        RecordResearchAttempt(
            confirmed.Map,
            confirmed.Result.Floor,
            frame,
            attempt,
            "side-entrance-scan",
            new FloorScaleSeed(
                sideSeed.LockedTransform,
                sideSeed.LockedTransform.ScaleX,
                IsCalibrated: false,
                Source: "side-entrance-feature"),
            [structureTuning.ScaleSearchRadius]);
        cancellationToken.ThrowIfCancellationRequested();

        var attemptConfidence = GetAttemptConfidence(attempt);
        var identityConsistent = attempt.Recognition is not null
            && confirmedSelection.Matches(
                confirmedCandidate,
                sideSeed,
                attempt.Recognition.Map.Id,
                attempt.Recognition.Result.MapId,
                attempt.Recognition.Result.Floor);
        var sessionAccepted = identityConsistent
            && attempt.Recognition is { } alignedResult
            && alignedResult.Result.Confidence
                >= Settings.SessionTuning.MediumConfidence;
        LogCollector.Append(
            MapLogCategory.StructureRegistration,
            sessionAccepted ? MapLogLevel.Info : MapLogLevel.Warning,
            $"侧门所选地图结构复核 · 地图 {confirmed.Map.SequenceNumber}"
                + $" · 侧门 {confirmedCandidate.MatchScore:P1}"
                + $" · 结构置信度 {attemptConfidence:P1}"
                + $" · {(sessionAccepted ? "通过" : "未通过")}",
            details: new()
            {
                ["selectedMapId"] = confirmedSelection.MapId,
                ["alignmentMapId"] = attempt.Recognition?.Map.Id,
                ["resultMapId"] = attempt.Recognition?.Result.MapId,
                ["sideEntranceConfidence"] = confirmedCandidate.MatchScore,
                ["structureConfidence"] = attemptConfidence,
                ["identityConsistent"] = identityConsistent,
                ["failureReason"] = attempt.FailureReason
            });

        if (!sessionAccepted)
        {
            var failureReason = !identityConsistent
                ? "对齐结果的地图身份与用户选择不一致。"
                : FormatAlignmentFailure(attempt, structureTuning);
            MergeDiagnostics(diagnostics, attempt.Diagnostics);
            _alignmentTrackingMode = attempt.Diagnostics.TrackingMode;
            TransitionSession(
                MapSessionState.LowConfidence,
                mapId: confirmed.Map.Id,
                floor: confirmed.Result.Floor,
                confidence: Math.Max(0d, attemptConfidence),
                detail: failureReason);
            PublishFailure(
                $"所选地图 {confirmed.Map.DisplayName} 未通过结构复核：{failureReason}"
                    + " 已保留用户选择，不会切换到其他候选地图。",
                showOverlay: true);
            return;
        }

        CurrentFloorKey = confirmed.Result.Floor;
        diagnostics.DetectedFloor = CurrentFloorKey;
        MergeDiagnostics(diagnostics, attempt.Diagnostics);
        _alignmentSession = sideSeed;

        var acceptedRecognition = attempt.Recognition!;
        TransitionSession(
            MapSessionState.FineLocating,
            mapId: confirmed.Map.Id,
            floor: CurrentFloorKey,
            locationMethod: ToLocationMethod(
                acceptedRecognition.Result.Source),
            confidence: acceptedRecognition.Result.Confidence,
            detail: "侧门扫描后的直接对齐已完成，正在确认稳定性。");

        if (acceptedRecognition.Result.Confidence
            < Settings.SessionTuning.MediumConfidence)
        {
            TransitionSession(
                MapSessionState.LowConfidence,
                mapId: confirmed.Map.Id,
                floor: CurrentFloorKey,
                confidence: acceptedRecognition.Result.Confidence,
                detail: "侧门扫描后的对齐置信度低于会话锁定阈值。");
            PublishFailure(
                $"侧门扫描后的对齐置信度 {acceptedRecognition.Result.Confidence:P1} 低于安全阈值 {Settings.SessionTuning.MediumConfidence:P1}。",
                showOverlay: true);
            return;
        }

        if (acceptedRecognition.Result.Confidence
                < Settings.SessionTuning.HighConfidence
            && !Settings.SessionTuning.SkipStabilityConfirmation)
        {
            TransitionSession(
                MapSessionState.Confirming,
                mapId: confirmed.Map.Id,
                floor: CurrentFloorKey,
                locationMethod: ToLocationMethod(
                    acceptedRecognition.Result.Source),
                confidence: acceptedRecognition.Result.Confidence,
                stableCandidateFrames: 1,
                detail: "侧门扫描后的中等置信度对齐正在等待连续帧确认。");
            acceptedRecognition = usesGateAlignment
                ? await ConfirmAlignmentCandidateAsync(
                    acceptedRecognition,
                    confirmed.Map.Id,
                    structureTuning,
                    cancellationToken,
                    previousAttempt: attempt)
                : await RunFloorWithoutGatesConfirmationAsync(
                    acceptedRecognition,
                    confirmed.Map.Id,
                    CurrentFloorKey,
                    structureTuning,
                    cancellationToken);
            if (acceptedRecognition is null)
            {
                PublishFailure(
                    "侧门扫描后的对齐未通过连续帧稳定性确认。",
                    showOverlay: true);
                return;
            }
        }

        if (!_matchSession.IsCurrent(match))
            return;
        if (!await ApplySelectedRecognitionAsync(
                acceptedRecognition,
                frame.ClientBounds,
                frame.ViewportBounds,
                frame.WindowHandle,
                diagnostics,
                cancellationToken))
        {
            return;
        }

        LogCollector.Append(
            MapLogCategory.ScanLifecycle,
            MapLogLevel.Info,
            $"侧门扫描后的直接对齐完成：{acceptedRecognition.Map.DisplayName} · "
                + $"来源 {acceptedRecognition.Result.Source} · "
                + $"置信度 {acceptedRecognition.Result.Confidence:P1}");
    }

    private async Task RunQuickScanAsync(
        CancellationToken cancellationToken,
        long inputTimestamp)
    {
        var total = Stopwatch.StartNew();
        var diagnostics = new MapScanDiagnostics();
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateRecognitionStart(out var validationFailure))
        {
            PublishFailure(validationFailure, showOverlay: true);
            return;
        }
        var match = _matchSession.Snapshot;
        await _scanGate.WaitAsync(cancellationToken);

        IsScanning = true;
        LastDiagnostics = null;
        ClearOverlayMap();
        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info, "快捷扫描开始",
            details: new() { ["scanType"] = "QuickScan", ["matchId"] = match.Version });
        if (!Settings.PersistentMiniMapEnabled)
            _overlay.Hide();
        LogCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info, "正在识别楼层……");
        StatusMessage = "正在冻结游戏地图画面……";
        NotifyStateChanged();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearMatchScopedMapState();
            CloseMapSession(
                "An explicit initial scan discarded the previous match-scoped map selection and alignment state.");
            await _settingsRepository.SaveAsync(Settings);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_matchSession.IsCurrent(match))
                return;
            var initialOperation = Settings.FirstScanStrategy
                == FirstScanStrategy.SideEntrance
                    ? MapAlignmentPrerequisiteKind.SideEntranceInitialScan
                    : MapAlignmentPrerequisiteKind.DoubleGateInitialScan;
            if (!MapMatchLifecycleRules.CanStart(
                    initialOperation,
                    _matchSession.Snapshot,
                    match,
                    Settings.FirstScanStrategy,
                    _selectedMapLease,
                    Settings.SelectedMapId,
                    _alignmentSession))
            {
                PublishFailure(
                    "The current match does not satisfy the configured initial-scan prerequisites.",
                    showOverlay: true);
                return;
            }
            // An explicit scan is only meaningful while the game's large map
            // is already visible. Synchronize that fact before choosing the
            // floor source: both skipped floor recognition and a floor locked
            // by the persistent mini-map bypass the recognizer below.
            _gameMapToggleState.MarkOpen();
            var floorLockedByMiniMap =
                TryGetDisplayedMiniMapFloorKey(out var displayedFloorKey);
            if (!floorLockedByMiniMap
                && !await TryRecognizeAutomaticFloorAsync(
                    inputTimestamp,
                    diagnostics,
                    cancellationToken,
                    intent: MapFloorRecognitionIntent.QuickScan))
            {
                return;
            }
            if (floorLockedByMiniMap)
                CurrentFloorKey = displayedFloorKey;
            if (CurrentFloorKey is { } quickFloorKey
                && !string.Equals(
                    quickFloorKey,
                    SelectedMap is null
                        ? "1f"
                        : MapFloorRules.GetPrimaryFloorKey(SelectedMap),
                    StringComparison.Ordinal))
            {
                PublishFailure(
                    $"The displayed mini-map floor '{quickFloorKey}' has no gates. "
                    + "Open the game map to run floor-specific structure alignment; other floor images were not scanned.",
                    showOverlay: true);
                return;
            }
            LogCollector.Append(MapLogCategory.FloorRecognition, MapLogLevel.Info,
                CurrentFloorKey == "2f" ? "楼层诊断完成" : "已识别为 1F",
                elapsedMs: diagnostics.FloorEndToEndMilliseconds,
                details: new() { ["floor"] = CurrentFloorKey, ["confidence"] = diagnostics.FloorConfidence });
            StatusMessage = CurrentFloorKey == "2f"
                ? "楼层结果仅作诊断；显式扫描优先，正在冻结游戏地图画面……"
                : "已识别为 1F，正在冻结游戏地图画面……";
            NotifyStateChanged();
            var stage = Stopwatch.StartNew();
            // Calibration defines the fixed on-screen map viewport. The map
            // content may move inside it, so the detected gate pair—not the
            // viewport rectangle—establishes the live map center.
            if (!_captureService.TryCaptureViewport(
                    Settings.MapViewportRegion!,
                    out var frame,
                    out var captureFailure)
                || frame is null)
            {
                stage.Stop();
                diagnostics.CaptureMilliseconds = stage.Elapsed.TotalMilliseconds;
                cancellationToken.ThrowIfCancellationRequested();
                PublishFailure(captureFailure, showOverlay: true);
                return;
            }
            stage.Stop();
            diagnostics.CaptureMilliseconds = stage.Elapsed.TotalMilliseconds;
            LogCollector.Append(MapLogCategory.ViewportCapture, MapLogLevel.Info, "游戏画面已冻结",
                elapsedMs: diagnostics.CaptureMilliseconds);
            RememberGameWindow(frame);
            cancellationToken.ThrowIfCancellationRequested();
            PublishStatus(
                "地图画面已冻结，正在检测双门……",
                new MapOverlayStatus(
                    MapOverlayStatusLevel.Scanning,
                    "正在识别地图",
                    "地图画面已冻结，正在检测双门……"),
                showOverlay: true);

            using (frame)
            {
                stage.Restart();
                await _recognition.RefreshCacheAsync();
                cancellationToken.ThrowIfCancellationRequested();
                stage.Stop();
                diagnostics.CacheMilliseconds = stage.Elapsed.TotalMilliseconds;
                diagnostics.ReadyMapCount = _recognition.ReadyMapCount;
                diagnostics.TotalMapCount = _recognition.TotalMapCount;
                if (_recognition.ReadyMapCount == 0)
                {
                    PublishFailure(
                        "没有可识别地图，请逐张补充一楼地图区域、大门和侧门。",
                        showOverlay: true);
                    return;
                }

                // 策略路由：侧门扫描走专属管线，双门对齐走原有管线
                if (Settings.FirstScanStrategy == FirstScanStrategy.SideEntrance)
                {
                    await RunSideEntranceScanWithFrameAsync(frame, match, diagnostics, cancellationToken);
                    return;
                }

                var tuning = Settings.RecognitionTuning.Clone();
                var attempt = await Task.Run(() =>
                    _recognition.Recognize(
                        frame,
                        Settings.OverlayAlignmentMode,
                        tuning,
                        mapClass: _matchSession.Snapshot.MapClass));
                cancellationToken.ThrowIfCancellationRequested();
                MergeDiagnostics(diagnostics, attempt.Diagnostics);
                RuntimeMapRecognition? recognition = attempt.Recognition;
                if (recognition is null && attempt.Choices.Count > 0)
                {
                    if (!Settings.PersistentMiniMapEnabled)
                        _overlay.Hide();
                    var selectedIndex = await MapManualCandidateWindow.ShowAsync(
                        frame,
                        attempt.Choices,
                        attempt.FailureReason,
                        cancellationToken);
                    if (selectedIndex is not null)
                        recognition = MapCvRecognitionService.ConfirmChoice(
                            attempt.Choices[selectedIndex.Value]);
                }
                if (recognition is null)
                {
                    LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                        $"快捷扫描失败：{attempt.FailureReason}");
                    PublishFailure(attempt.FailureReason, showOverlay: true);
                    return;
                }
                var validation = await ValidateInitialRecognitionAsync(
                    frame,
                    recognition,
                    cancellationToken);
                MergeDiagnostics(diagnostics, validation.Diagnostics);
                if (validation.Recognition is null)
                {
                    PublishFailure(
                        $"双门地图身份已识别，但静态结构复核失败：{validation.FailureReason}",
                        showOverlay: true);
                    return;
                }
                var acceptedRecognition =
                    await EnforceLockConfidenceAsync(
                        validation.Recognition,
                        cancellationToken,
                        previousAttempt: validation);

                cancellationToken.ThrowIfCancellationRequested();
                if (!_matchSession.IsCurrent(match))
                    return;
                if (!await ApplySelectedRecognitionAsync(
                    acceptedRecognition,
                    frame.ClientBounds,
                    frame.ViewportBounds,
                    frame.WindowHandle,
                    diagnostics,
                    cancellationToken))
                {
                    return;
                }
                total.Stop();
                diagnostics.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
                LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                    $"快捷扫描完成：{acceptedRecognition.Map.DisplayName} · 总计 {diagnostics.TotalMilliseconds:F0}ms",
                    elapsedMs: diagnostics.TotalMilliseconds,
                    details: new() { ["mapId"] = acceptedRecognition.Map.Id, ["confidence"] = acceptedRecognition.Result.Confidence, ["source"] = acceptedRecognition.Result.Source.ToString() });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!Settings.PersistentMiniMapEnabled)
                _overlay.Hide();
        }
        catch (Exception exception)
        {
            LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                $"快捷扫描异常：{exception.Message}");
            PublishFailure($"扫描失败：{exception.Message}", showOverlay: true);
        }
        finally
        {
            FinishScan(total, diagnostics);
        }
    }

    private async Task RunSelectedMapAlignmentAsync(
        CancellationToken cancellationToken,
        long inputTimestamp,
        MapGameToggleTransition openTransition,
        StableViewportCaptureResult? stableFrames = null,
        MapPreAlignmentTiming? preTiming = null)
    {
        var total = Stopwatch.StartNew();
        var diagnostics = new MapScanDiagnostics();
        if (preTiming is { } pt)
        {
            diagnostics.InputToAlignmentStartMilliseconds = pt.InputToAlignmentStartMs;
            diagnostics.OpeningAnimationWaitMilliseconds = pt.AnimationWaitMs;
            diagnostics.StableViewportWaitMilliseconds = pt.StableViewportWaitMs;
            diagnostics.StableViewportCaptureMilliseconds = pt.StableViewportCaptureMs;
            diagnostics.StableViewportAttempts = pt.StableViewportAttempts;
            diagnostics.StableViewportSuccessfulCaptures = pt.StableViewportSuccessfulCaptures;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gameMapToggleState.IsCurrent(openTransition))
            return;
        await InitializeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gameMapToggleState.IsCurrent(openTransition))
            return;
        if (!TryValidateRecognitionStart(out var validationFailure))
        {
            PublishAlignmentFailure(validationFailure);
            return;
        }
        if (!await _scanGate.WaitAsync(0))
            return;

        IsScanning = true;
        LastDiagnostics = null;
        ClearOverlayMap();
        if (!Settings.PersistentMiniMapEnabled)
            _overlay.Hide();
        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info, "开图对齐开始",
            details: new() { ["mapId"] = Settings.SelectedMapId, ["scanType"] = "MapOpen" });
        StatusMessage = "正在识别当前楼层……";
        NotifyStateChanged();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primaryFloorKey = SelectedMap is null
                ? "1f"
                : MapFloorRules.GetPrimaryFloorKey(SelectedMap);
            var floorLockedByDisplayedMiniMap =
                TryGetDisplayedMiniMapFloorKey(out var displayedFloorKey);
            if (floorLockedByDisplayedMiniMap
                && !await VerifyNativeMapIsOpenAsync(
                    inputTimestamp,
                    diagnostics,
                    cancellationToken))
            {
                // 持久小地图楼层提示未能通过原生地图验证（超时或指示器
                // 不可见）。回退到常规楼层识别，而非立即报错——侧门扫描
                // 等低置信度对齐遗留的 LastRecognition 不应阻止新一轮
                // 正常开图对齐。
                LogCollector.Append(
                    MapLogCategory.FloorRecognition,
                    MapLogLevel.Warning,
                    "持久楼层提示验证未通过，回退至常规楼层识别。");
                _overlay.ClearPersistentMiniMap();
                floorLockedByDisplayedMiniMap = false;
            }
            if (floorLockedByDisplayedMiniMap
                && !string.Equals(
                    displayedFloorKey,
                    primaryFloorKey,
                    StringComparison.Ordinal))
            {
                // A displayed non-primary floor is locked to its gate-free path.
                await RunFloorWithoutGatesAlignmentAsync(
                    cancellationToken,
                    inputTimestamp,
                    openTransition,
                    diagnostics,
                    stableFrames,
                    displayedFloorKey);
                return;
            }
            if (!floorLockedByDisplayedMiniMap
                && !await TryRecognizeAutomaticFloorAsync(
                    inputTimestamp,
                    diagnostics,
                    cancellationToken,
                    failureTitle: "地图对齐失败"))
            {
                return;
            }
            if (floorLockedByDisplayedMiniMap)
            {
                CurrentFloorKey = displayedFloorKey;
                diagnostics.DetectedFloor = displayedFloorKey;
                diagnostics.FloorConfidence = 1d;
            }
            cancellationToken.ThrowIfCancellationRequested();
            primaryFloorKey = SelectedMap is null
                ? "1f"
                : MapFloorRules.GetPrimaryFloorKey(SelectedMap);
            if (CurrentFloorKey is { } detectedFloorKey
                && !string.Equals(
                    detectedFloorKey,
                    primaryFloorKey,
                    StringComparison.Ordinal))
            {
                // With no display lock, the automatic 1/2 indicator maps to
                // the corresponding ordered floor and selects its exact path.
                await RunFloorWithoutGatesAlignmentAsync(
                    cancellationToken,
                    inputTimestamp,
                    openTransition,
                    diagnostics,
                    stableFrames,
                    detectedFloorKey);
                return;
            }
            if (!_gameMapToggleState.IsCurrent(openTransition)
                || SessionSnapshot.State == MapSessionState.Closed)
            {
                return;
            }
            TransitionSession(
                MapSessionState.IdentifyingMap,
                mapId: Settings.SelectedMapId,
                floor: CurrentFloorKey,
                detail: "楼层已确认，正在确认当前地图 ID。");
            if (Settings.SelectedMapId is not { } selectedMapId
                || SelectedMap is null)
            {
                _alignmentTrackingMode = MapAlignmentTrackingMode.None;
                PublishAlignmentFailure(
                    "已识别为 1F，但尚未选择地图；请先使用快捷扫描或手动识别。",
                    showOverlay: true);
                return;
            }
            if (!_selectedMapLease.IsCurrent(
                    _matchSession.Snapshot,
                    selectedMapId))
            {
                _alignmentTrackingMode = MapAlignmentTrackingMode.None;
                PublishAlignmentFailure(
                    "The selected map belongs to an earlier match. Run the configured initial scan for this match before alignment.",
                    showOverlay: true);
                return;
            }
            StatusMessage =
                $"已识别为 1F，正在刷新 {SelectedMap.DisplayName} 的地图对齐……";
            NotifyStateChanged();
            CapturedGameFrame? frame;
            bool ownsFrame;
            var captureStage = Stopwatch.StartNew();
            if (stableFrames?.PrimaryFrame is { } primary)
            {
                // Reuse the stability-detected primary frame — no new capture.
                diagnostics.AlignmentCaptureMilliseconds = 0d;
                frame = primary;
                ownsFrame = false;
                LogCollector.Append(MapLogCategory.ViewportCapture, MapLogLevel.Info,
                    "复用稳定帧进行对齐 · 截帧 0ms");
            }
            else
            {
                if (!_captureService.TryCaptureViewport(
                        Settings.MapViewportRegion!,
                        out frame,
                        out var captureFailure)
                    || frame is null)
                {
                    captureStage.Stop();
                    diagnostics.CaptureMilliseconds = captureStage.Elapsed.TotalMilliseconds;
                    PublishAlignmentFailure(captureFailure);
                    return;
                }
                diagnostics.AlignmentCaptureMilliseconds = captureStage.Elapsed.TotalMilliseconds;
                diagnostics.CaptureMilliseconds = diagnostics.AlignmentCaptureMilliseconds;
                ownsFrame = true;
                LogCollector.Append(MapLogCategory.ViewportCapture, MapLogLevel.Info, "游戏画面已冻结",
                    elapsedMs: diagnostics.CaptureMilliseconds);
            }
            captureStage.Stop();
            RememberGameWindow(frame);
            PublishStatus(
                $"正在使用已选择的 {SelectedMap.DisplayName} 刷新对齐……",
                new MapOverlayStatus(
                    MapOverlayStatusLevel.Scanning,
                    "正在刷新地图对齐",
                    $"只对齐 {SelectedMap.DisplayName}，不会重新选择地图。"),
                showOverlay: true);

            using (var frameDispose = ownsFrame ? frame : null)
            {
                var preAlignTimer = Stopwatch.StartNew();
                captureStage.Restart();
                var previousUpdatedAt = SelectedMap.UpdatedAt;
                await _recognition.RefreshCacheAsync();
                await RefreshSelectedMapReferenceAsync();
                captureStage.Stop();
                diagnostics.CacheMilliseconds = captureStage.Elapsed.TotalMilliseconds;
                LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                    $"缓存刷新完成 · {captureStage.Elapsed.TotalMilliseconds:F0}ms",
                    elapsedMs: captureStage.Elapsed.TotalMilliseconds);
                diagnostics.ReadyMapCount = _recognition.ReadyMapCount;
                diagnostics.TotalMapCount = _recognition.TotalMapCount;
                if (SelectedMap is null
                    || Settings.SelectedMapId != selectedMapId)
                {
                    _alignmentTrackingMode = MapAlignmentTrackingMode.None;
                    PublishAlignmentFailure(
                        "先前选择的地图已不存在；只清除了失效的地图序号，没有修改其他地图数据。",
                        showOverlay: true);
                    return;
                }
                if (SelectedMap.UpdatedAt != previousUpdatedAt)
                    InvalidateAlignment(MapAlignmentTrackingMode.Lost);

                var signature = CreateWindowSignature(frame);
                // 已有的本图会话优先于标定种子。侧门扫描刚用特征匹配锁定过
                // 缩放与平移，而 CreateAlignmentSeed 在缺少本窗口标定记录时
                // 返回 null —— 无条件覆盖会丢掉那个种子，AlignSelected 拿到
                // null 就退回"尚未完成双门缩放锁定"，侧门链路等于从未接上。
                // 跨图或改图后的旧会话仍然作废，否则会挡住本图的标定种子。
                var reusableSession = _alignmentSession is { } existing
                    && existing.MapId == SelectedMap.Id
                    && existing.MapUpdatedAt == SelectedMap.UpdatedAt
                        ? existing
                        : null;
                _alignmentSession = reusableSession
                    ?? CreateAlignmentSeed(
                        SelectedMap,
                        frame,
                        signature);
                var predictedOrigin = PredictViewportOrigin(
                    SelectedMap,
                    frame,
                    _alignmentSession);
                var tuning = Settings.RecognitionTuning.Clone();
                var structureTuning =
                    Settings.StructureRegistrationTuning.Clone();
                cancellationToken.ThrowIfCancellationRequested();
                if (!_gameMapToggleState.IsCurrent(openTransition)
                    || SessionSnapshot.State == MapSessionState.Closed)
                {
                    return;
                }
                TransitionSession(
                    MapSessionState.CoarseLocating,
                    mapId: selectedMapId,
                    floor: CurrentFloorKey,
                    detail: "按双门、单门、静态结构顺序搜索本次视口。");
                var searchCtx = BuildAlignmentSearchContext(
                    _alignmentSession,
                    tuning);
                preAlignTimer.Stop();
                LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                    $"对齐前置完成 · {preAlignTimer.Elapsed.TotalMilliseconds:F0}ms",
                    elapsedMs: preAlignTimer.Elapsed.TotalMilliseconds,
                    details: new()
                    {
                        ["cacheMs"] = captureStage.Elapsed.TotalMilliseconds,
                    });
                var attempt = await Task.Run(
                    () => _recognition.AlignSelected(
                        frame,
                        selectedMapId,
                        _alignmentSession,
                        Settings.OverlayAlignmentMode,
                        tuning,
                        structureTuning,
                        _lastTrustedPlayerPoint,
                        predictedOrigin,
                        BuildLiveIgnoreRegions(frame),
                        alignmentSearchContext: searchCtx,
                        nativeScaleChangeRatio:
                            Settings.SessionTuning.NativeScaleChangeRatio,
                        mapClass: _matchSession.Snapshot.MapClass),
                    cancellationToken).ConfigureAwait(false);
                RecordResearchAttempt(
                    SelectedMap,
                    primaryFloorKey,
                    frame,
                    attempt,
                    floorLockedByDisplayedMiniMap
                        ? "displayed-mini-map"
                        : "automatic-indicator");
                cancellationToken.ThrowIfCancellationRequested();
                if (!_gameMapToggleState.IsCurrent(openTransition)
                    || SessionSnapshot.State == MapSessionState.Closed)
                {
                    return;
                }
                MergeDiagnostics(diagnostics, attempt.Diagnostics);
                if (attempt.Recognition is null)
                {
                    var identityRecheck =
                        attempt.Diagnostics.GateCandidateCount >= 2
                            ? await TryRecheckMapIdentityAsync(
                                frame,
                                selectedMapId,
                                structureTuning,
                                cancellationToken)
                            : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsOpenPipelineCurrent(openTransition))
                        return;
                    if (identityRecheck is not null)
                    {
                        MergeDiagnostics(
                            diagnostics,
                            identityRecheck.Diagnostics);
                        var recheckedRecognition =
                            identityRecheck.Recognition!;
                        if (recheckedRecognition.Result.Confidence
                                < Settings.SessionTuning.HighConfidence
                            && recheckedRecognition.Result.Confidence
                                >= Settings.SessionTuning.MediumConfidence)
                        {
                            TransitionSession(
                                MapSessionState.FineLocating,
                                mapId: recheckedRecognition.Map.Id,
                                floor: recheckedRecognition.Result.Floor,
                                locationMethod: ToLocationMethod(
                                    recheckedRecognition.Result.Source),
                                confidence:
                                    recheckedRecognition.Result.Confidence,
                                detail:
                                    "The full-map ID recheck passed structure validation.");
                            TransitionSession(
                                MapSessionState.Confirming,
                                mapId: recheckedRecognition.Map.Id,
                                floor: recheckedRecognition.Result.Floor,
                                locationMethod: ToLocationMethod(
                                    recheckedRecognition.Result.Source),
                                confidence:
                                    recheckedRecognition.Result.Confidence,
                                stableCandidateFrames: 1,
                                detail:
                                    "A conflicting dual-anchor map ID is being confirmed.");
                            recheckedRecognition =
                                await ConfirmAlignmentCandidateAsync(
                                    recheckedRecognition,
                                    recheckedRecognition.Map.Id,
                                    structureTuning,
                                    cancellationToken,
                                    previousAttempt: identityRecheck,
                                    preparedConfirmationFrame: stableFrames?.ConfirmationFrame)
                                ?? throw new MapAlignmentConfirmationException(
                                    "The full-map ID recheck did not remain stable.");
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!IsOpenPipelineCurrent(openTransition))
                                return;
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!IsOpenPipelineCurrent(openTransition))
                            return;
                        if (!await ApplySelectedRecognitionAsync(
                            recheckedRecognition,
                            frame.ClientBounds,
                            frame.ViewportBounds,
                            frame.WindowHandle,
                            diagnostics,
                            cancellationToken))
                        {
                            return;
                        }
                        total.Stop();
                        diagnostics.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
                        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                            $"开图对齐完成：{recheckedRecognition.Map.DisplayName} · 总计 {diagnostics.TotalMilliseconds:F0}ms",
                            elapsedMs: diagnostics.TotalMilliseconds,
                            details: new() { ["mapId"] = recheckedRecognition.Map.Id, ["confidence"] = recheckedRecognition.Result.Confidence });
                        return;
                    }
                    _alignmentTrackingMode = attempt.Diagnostics.TrackingMode;
                    if (_alignmentSession is not null
                        && attempt.Diagnostics.TrackingMode
                            == MapAlignmentTrackingMode.HoldingLastTransform)
                    {
                        _alignmentSession =
                            _alignmentSession.Hold(attempt.StructureResult);
                    }
                    TransitionSession(
                        MapSessionState.LowConfidence,
                        mapId: selectedMapId,
                        floor: CurrentFloorKey,
                        confidence: GetAttemptConfidence(attempt),
                        detail: FormatAlignmentFailure(
                            attempt,
                            structureTuning));
                    PublishAlignmentFailure(
                        FormatAlignmentFailure(attempt, structureTuning));
                    return;
                }

                TransitionSession(
                    MapSessionState.FineLocating,
                    mapId: selectedMapId,
                    floor: CurrentFloorKey,
                    locationMethod: ToLocationMethod(
                        attempt.Recognition.Result.Source),
                    confidence: attempt.Recognition.Result.Confidence,
                    detail: "粗定位已完成，正在执行仅平移精修。");
                diagnostics.FirstCandidateMilliseconds = total.Elapsed.TotalMilliseconds;
                var acceptedRecognition = attempt.Recognition;
                if (acceptedRecognition.Result.Confidence
                    < Settings.SessionTuning.MediumConfidence)
                {
                    TransitionSession(
                        MapSessionState.LowConfidence,
                        mapId: selectedMapId,
                        floor: CurrentFloorKey,
                        confidence: acceptedRecognition.Result.Confidence,
                        detail: "最终置信度低于会话锁定阈值。");
                    PublishAlignmentFailure(
                        $"本次配准置信度 "
                        + $"{acceptedRecognition.Result.Confidence:P1} "
                        + $"低于安全阈值 "
                        + $"{Settings.SessionTuning.MediumConfidence:P1}。");
                    return;
                }
                if (acceptedRecognition.Result.Confidence
                    < Settings.SessionTuning.HighConfidence
                    && !Settings.SessionTuning.SkipStabilityConfirmation)
                {
                    TransitionSession(
                        MapSessionState.Confirming,
                        mapId: selectedMapId,
                        floor: CurrentFloorKey,
                        locationMethod: ToLocationMethod(
                            acceptedRecognition.Result.Source),
                        confidence: acceptedRecognition.Result.Confidence,
                        stableCandidateFrames: 1,
                        detail: "中等置信度结果正在等待连续帧确认。");
                    acceptedRecognition =
                        await ConfirmAlignmentCandidateAsync(
                            acceptedRecognition,
                            selectedMapId,
                            structureTuning,
                            cancellationToken,
                            previousAttempt: attempt,
                            preparedConfirmationFrame: stableFrames?.ConfirmationFrame)
                        ?? throw new MapAlignmentConfirmationException(
                            "中等置信度候选未通过连续帧确认。");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsOpenPipelineCurrent(openTransition))
                        return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOpenPipelineCurrent(openTransition))
                    return;
                if (!await ApplyRecognitionAsync(
                    acceptedRecognition,
                    frame.ClientBounds,
                    frame.ViewportBounds,
                    frame.WindowHandle,
                    diagnostics,
                    cancellationToken))
                {
                    return;
                }
                total.Stop();
                diagnostics.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
                diagnostics.AlignmentPipelineMilliseconds = diagnostics.TotalMilliseconds;
                diagnostics.InputToLockedMilliseconds =
                    (Stopwatch.GetTimestamp() - inputTimestamp)
                    * 1000d / Stopwatch.Frequency;
                LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                    $"开图对齐完成：{acceptedRecognition.Map.DisplayName} · 管线 {diagnostics.AlignmentPipelineMilliseconds:F0}ms · 端到端 {diagnostics.InputToLockedMilliseconds:F0}ms",
                    elapsedMs: diagnostics.InputToLockedMilliseconds,
                    details: new()
                    {
                        ["mapId"] = acceptedRecognition.Map.Id,
                        ["confidence"] = acceptedRecognition.Result.Confidence,
                        ["pipelineMs"] = diagnostics.AlignmentPipelineMilliseconds,
                        ["inputToLockedMs"] = diagnostics.InputToLockedMilliseconds
                    });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsOpenPipelineCurrent(openTransition))
            {
                if (Settings.PersistentMiniMapEnabled)
                    TryRestorePersistentMiniMap();
                else
                    _overlay.Hide();
            }
        }
        catch (MapAlignmentConfirmationException exception)
        {
            if (IsOpenPipelineCurrent(openTransition))
            {
                TransitionSession(
                    MapSessionState.LowConfidence,
                    detail: exception.Message);
                PublishAlignmentFailure(exception.Message);
            }
        }
        catch (Exception exception)
        {
            if (!IsOpenPipelineCurrent(openTransition))
                return;

            LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Error,
                $"开图对齐异常：{exception.Message}");
            _alignmentTrackingMode = _alignmentSession is null
                ? MapAlignmentTrackingMode.NeedsGatePair
                : MapAlignmentTrackingMode.HoldingLastTransform;
            if (_alignmentSession is not null)
                _alignmentSession = _alignmentSession.Hold(null);
            PublishAlignmentFailure($"地图对齐刷新失败：{exception.Message}");
        }
        finally
        {
            if (IsOpenPipelineCurrent(openTransition))
            {
                var sessionState = SessionSnapshot.State;
                if (sessionState != MapSessionState.Locked
                    && sessionState != MapSessionState.Closed)
                {
                    if (sessionState is MapSessionState.WaitingForStableFrames
                        or MapSessionState.IdentifyingMap
                        or MapSessionState.CoarseLocating
                        or MapSessionState.FineLocating
                        or MapSessionState.Confirming)
                    {
                        TransitionSession(
                            MapSessionState.LowConfidence,
                            detail:
                                "Alignment ended without a safe background lock.");
                    }
                    AbandonOpenAlignment(
                        "Alignment ended without locking a background.");
                }
            }
            FinishScan(total, diagnostics);
        }
    }

    public Task RunManualRecognitionAsync() =>
        StartTrackedOperation(
            cancellationToken => RunManualRecognitionForCurrentMatchAsync(
                Stopwatch.GetTimestamp(),
                cancellationToken));

    private async Task RunManualRecognitionForCurrentMatchAsync(
        long inputTimestamp,
        CancellationToken lifetimeToken)
    {
        _gameMapRefreshCancellation?.Cancel();
        var cancellation = BeginExplicitRecognition(
            lifetimeToken,
            MapFloorRecognitionIntent.ManualRecognition);
        if (cancellation is null)
            return;
        try
        {
            await RunManualRecognitionAsync(
                inputTimestamp,
                cancellation.Token);
        }
        finally
        {
            CompleteExplicitRecognition(cancellation);
        }
    }

    private CancellationTokenSource? BeginExplicitRecognition(
        CancellationToken lifetimeToken,
        MapFloorRecognitionIntent intent)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken,
            _matchCancellation?.Token ?? CancellationToken.None);
        var priority =
            MapFloorRecognitionRules.GetOperationPriority(intent);
        lock (_explicitRecognitionGate)
        {
            if (_explicitRecognitionCancellation is not null)
            {
                var mayPreempt =
                    priority > _explicitRecognitionPriority
                    || (priority == _explicitRecognitionPriority
                        && intent
                            == MapFloorRecognitionIntent.ManualRecognition);
                if (!mayPreempt)
                {
                    cancellation.Dispose();
                    return null;
                }
                _explicitRecognitionCancellation.Cancel();
            }
            _explicitRecognitionCancellation = cancellation;
            _explicitRecognitionPriority = priority;
            return cancellation;
        }
    }

    private void CompleteExplicitRecognition(
        CancellationTokenSource cancellation)
    {
        lock (_explicitRecognitionGate)
        {
            if (ReferenceEquals(
                    _explicitRecognitionCancellation,
                    cancellation))
            {
                _explicitRecognitionCancellation = null;
                _explicitRecognitionPriority = -1;
            }
        }
        cancellation.Dispose();
    }

    private async Task RunManualRecognitionAsync(
        long inputTimestamp,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var diagnostics = new MapScanDiagnostics();
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateRecognitionStart(out var validationFailure))
        {
            PublishFailure(validationFailure, showOverlay: true);
            return;
        }
        var match = _matchSession.Snapshot;
        await _scanGate.WaitAsync(cancellationToken);

        IsScanning = true;
        LastDiagnostics = null;
        ClearOverlayMap();
        if (!Settings.PersistentMiniMapEnabled)
            _overlay.Hide();
        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info, "手动识别开始",
            details: new() { ["scanType"] = "ManualRecognition" });
        StatusMessage = "正在冻结游戏画面，准备手动框选双门……";
        NotifyStateChanged();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the game-map toggle in sync even when floor recognition is
            // skipped or supplied by the persistent mini-map.
            _gameMapToggleState.MarkOpen();
            var floorLockedByDisplayedMiniMap =
                TryGetDisplayedMiniMapFloorKey(out var displayedFloorKey);
            if (!floorLockedByDisplayedMiniMap
                && !await TryRecognizeAutomaticFloorAsync(
                    inputTimestamp,
                    diagnostics,
                    cancellationToken,
                    intent:
                        MapFloorRecognitionIntent.ManualRecognition))
            {
                return;
            }
            if (floorLockedByDisplayedMiniMap)
                CurrentFloorKey = displayedFloorKey;
            if (CurrentFloorKey is { } manualFloorKey
                && !string.Equals(
                    manualFloorKey,
                    SelectedMap is null
                        ? "1f"
                        : MapFloorRules.GetPrimaryFloorKey(SelectedMap),
                    StringComparison.Ordinal))
            {
                PublishFailure(
                    $"Floor '{manualFloorKey}' uses gate-free structure alignment. "
                    + "Manual gate selection is available only on the primary floor.",
                    showOverlay: true);
                return;
            }
            StatusMessage = CurrentFloorKey == "2f"
                ? "F4 手动识别优先于楼层结果，正在冻结游戏画面……"
                : "已识别为 1F，正在冻结游戏画面，准备手动框选双门……";
            NotifyStateChanged();
            var stage = Stopwatch.StartNew();
            if (!_captureService.TryCaptureClient(out var frame, out var failureReason)
                || frame is null)
            {
                stage.Stop();
                diagnostics.CaptureMilliseconds = stage.Elapsed.TotalMilliseconds;
                PublishFailure(failureReason, showOverlay: true);
                return;
            }
            stage.Stop();
            diagnostics.CaptureMilliseconds = stage.Elapsed.TotalMilliseconds;
            RememberGameWindow(frame);

            using (frame)
            {
                var viewportBounds = DwrGameWindowCaptureService.GetViewportBounds(
                    frame.ClientBounds,
                    Settings.MapViewportRegion!);
                PublishStatus(
                    "请依次框选大门和侧门。",
                    new MapOverlayStatus(
                        MapOverlayStatusLevel.ManualSelection,
                        "手动识别",
                        "请依次框选大门和侧门。",
                        "右键或 Backspace 撤销，Esc 取消"),
                    showOverlay: true);
                _controlPanel.Hide();
                _manualSelectionActive = true;
                ManualGateSelectionResult? selection;
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
                    PublishStatus(
                        "已取消手动识别。",
                        new MapOverlayStatus(
                            MapOverlayStatusLevel.Warning,
                            "手动识别已取消",
                            "未更改当前地图结果。"),
                        showOverlay: true);
                    return;
                }

                stage.Restart();
                await _recognition.RefreshCacheAsync();
                cancellationToken.ThrowIfCancellationRequested();
                stage.Stop();
                diagnostics.CacheMilliseconds = stage.Elapsed.TotalMilliseconds;
                diagnostics.ReadyMapCount = _recognition.ReadyMapCount;
                diagnostics.TotalMapCount = _recognition.TotalMapCount;
                var tuning = Settings.RecognitionTuning.Clone();
                var attempt = await Task.Run(
                    () => _recognition.RecognizeManual(
                        viewportBounds,
                        selection.MainGateBounds,
                        selection.SideGateBounds,
                        Settings.OverlayAlignmentMode,
                        tuning,
                        mapClass: _matchSession.Snapshot.MapClass),
                    cancellationToken);
                MergeDiagnostics(diagnostics, attempt.Diagnostics);

                RuntimeMapRecognition? recognition = attempt.Recognition;
                if (recognition is null && attempt.Choices.Count > 0)
                {
                    if (!Settings.PersistentMiniMapEnabled)
                        _overlay.Hide();
                    var selectedIndex = await MapManualCandidateWindow.ShowAsync(
                        frame,
                        attempt.Choices,
                        attempt.FailureReason,
                        cancellationToken);
                    if (selectedIndex is null)
                    {
                        PublishStatus(
                            "已取消候选地图确认。",
                            new MapOverlayStatus(
                                MapOverlayStatusLevel.Warning,
                                "手动识别已取消",
                                "没有选择地图。"),
                            showOverlay: true);
                        return;
                    }
                    recognition = MapCvRecognitionService.ConfirmChoice(
                        attempt.Choices[selectedIndex.Value]);
                }
                if (recognition is null)
                {
                    PublishFailure(attempt.FailureReason, showOverlay: true);
                    return;
                }
                var validation = await ValidateInitialRecognitionAsync(
                    frame,
                    recognition,
                    cancellationToken);
                MergeDiagnostics(diagnostics, validation.Diagnostics);
                if (validation.Recognition is null)
                {
                    diagnostics.UsedForcedBestResult = true;
                    LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Warning,
                        $"手动识别：静态结构复核失败（{validation.FailureReason}），回退到门点匹配结果。");
                }
                else
                {
                    recognition = validation.Recognition;
                }
                EnsureSessionCanLock(recognition);

                if (!_matchSession.IsCurrent(match))
                    return;
                if (!await ApplySelectedRecognitionAsync(
                    recognition,
                    frame.ClientBounds,
                    frame.ViewportBounds,
                    frame.WindowHandle,
                    diagnostics,
                    cancellationToken))
                {
                    return;
                }
                total.Stop();
                diagnostics.TotalMilliseconds = total.Elapsed.TotalMilliseconds;
                LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                    $"手动识别完成：{recognition.Map.DisplayName} · 总计 {diagnostics.TotalMilliseconds:F0}ms",
                    elapsedMs: diagnostics.TotalMilliseconds,
                    details: new() { ["mapId"] = recognition.Map.Id, ["source"] = "Manual" });
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            if (Settings.PersistentMiniMapEnabled)
                TryRestorePersistentMiniMap();
            else
                _overlay.Hide();
        }
        catch (Exception exception)
        {
            PublishFailure($"手动识别失败：{exception.Message}", showOverlay: true);
        }
        finally
        {
            FinishScan(total, diagnostics);
        }
    }

    public void ToggleOverlay()
    {
        if (_disposed
            || !Settings.IsEnabled
            || !_matchSession.Snapshot.IsStarted)
            return;
        try
        {
            if (!_overlay.HasMap)
            {
                // 小地图常驻时不关，只有纯状态文字才直接关
                if (_overlay.IsVisible
                    && !Settings.PersistentMiniMapEnabled)
                {
                    _overlay.Hide();
                    StatusMessage = "识别图层已隐藏。";
                    NotifyStateChanged();
                    return;
                }
                if (!_captureService.TryGetForegroundClientBounds(
                        out var bounds,
                        out var windowHandle,
                        out var failureReason))
                {
                    StatusMessage = failureReason;
                    NotifyStateChanged();
                    return;
                }
                _lastGameBounds = bounds;
                _lastGameWindowHandle = windowHandle;
                _overlay.UpdateStatus(
                    _currentOverlayStatus,
                    bounds,
                    windowHandle,
                    Settings.ShowOverlayStatus,
                    showImmediately: false);
            }
            _overlay.Toggle();
            StatusMessage = _overlay.IsVisible ? "识别图层已显示。" : "识别图层已隐藏。";
        }
        catch (Exception exception)
        {
            _overlay.Hide();
            StatusMessage = $"识别图层无法安全显示：{exception.Message}";
        }
        NotifyStateChanged();
    }

    public bool TryRestartElevated(out string failureReason)
    {
        var started = GameProcessIntegrityService.TryRestartElevated(out failureReason);
        if (!started)
        {
            StatusMessage = failureReason;
            NotifyStateChanged();
        }
        return started;
    }

    private async Task<bool> ApplySelectedRecognitionAsync(
        RuntimeMapRecognition recognition,
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        IntPtr windowHandle,
        MapScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mapChanged = SelectedMap?.Id != recognition.Map.Id;
        var proposed = Settings.Clone();
        proposed.SelectedMapId = recognition.Map.Id;
        await _settingsRepository.SaveAsync(proposed);
        cancellationToken.ThrowIfCancellationRequested();
        Settings = proposed;
        SelectedMap = recognition.Map.Clone();
        if (mapChanged)
            _manualFloorOverrideKey = null;
        _alignmentSession = null;
        return await ApplyRecognitionAsync(
            recognition,
            clientBounds,
            viewportBounds,
            windowHandle,
            diagnostics,
            cancellationToken);
    }

    private async Task<bool> ApplyRecognitionAsync(
        RuntimeMapRecognition recognition,
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        IntPtr windowHandle,
        MapScanDiagnostics diagnostics,
        CancellationToken cancellationToken,
        bool allowCalibrationUpdate = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_matchSession.Snapshot.IsStarted)
            return false;
        var transform = recognition.Result.OverlayTransform
            ?? throw new InvalidOperationException(
                "The accepted map recognition has no overlay transform.");
        var isManualSource = recognition.Result.Source
            is MapRecognitionSource.ManualGateSelection
            or MapRecognitionSource.UserConfirmed;
        if (!isManualSource)
        {
            if (recognition.Result.WasForcedBestResult
                || recognition.Result.ReusedLastTransform)
            {
                throw new InvalidOperationException(
                    "Forced or reused candidates cannot lock a map-open session.");
            }
            if (recognition.Result.Confidence
                < Settings.SessionTuning.MediumConfidence)
            {
                throw new InvalidOperationException(
                    "The accepted map recognition is below the safe lock threshold.");
            }
        }
        EnsureSessionCanLock(recognition);
        var priorAlignmentSession = _alignmentSession;
        var lockSnapshotAtObservation = SessionSnapshot;
        var updatesExistingLock = lockSnapshotAtObservation.IsLocked
            && lockSnapshotAtObservation.MapId == recognition.Map.Id
            && string.Equals(
                lockSnapshotAtObservation.Floor,
                recognition.Result.Floor,
                StringComparison.Ordinal)
            && priorAlignmentSession is not null
            && priorAlignmentSession.MapId == recognition.Map.Id
            && priorAlignmentSession.MapUpdatedAt == recognition.Map.UpdatedAt
            && string.Equals(
                priorAlignmentSession.FloorKey,
                recognition.Result.Floor,
                StringComparison.Ordinal)
            && recognition.Result.Source is (
                MapRecognitionSource.SingleGateTracking
                or MapRecognitionSource.AuxiliaryAnchorTracking
                or MapRecognitionSource.StructureMatching);
        var continuousObservation = updatesExistingLock
            ? priorAlignmentSession!.BeginContinuousObservation(
                recognition.Map,
                lockSnapshotAtObservation)
            : null;
        var observedStableFrames = _candidateStability.Count;
        if (!isManualSource
            && !MapSessionRules.HasRequiredLockStability(
                recognition.Result.Confidence,
                Settings.SessionTuning.HighConfidence,
                Settings.SessionTuning.SkipStabilityConfirmation,
                observedStableFrames,
                Settings.SessionTuning.MediumConfidenceFrames))
        {
            LogCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                "Alignment lock commit rejected because stable-frame confirmation is incomplete.",
                details: new()
                {
                    ["confidence"] = recognition.Result.Confidence,
                    ["highConfidence"] = Settings.SessionTuning.HighConfidence,
                    ["observedStableFrames"] = observedStableFrames,
                    ["requiredStableFrames"] =
                        Settings.SessionTuning.MediumConfidenceFrames,
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = recognition.Result.Floor
                });
            throw new InvalidOperationException(
                "A medium-confidence alignment observation has not completed stable-frame confirmation.");
        }
        var committedStableFrames = recognition.Result.Confidence
                >= Settings.SessionTuning.HighConfidence
            ? 1
            : Math.Max(1, observedStableFrames);
        var similarity = MapSimilarityTransform.FromOverlay(transform);
        if (!similarity.IsValid)
            throw new InvalidOperationException(
                "The accepted map recognition transform is not finite.");
        var viewportReferencePoint = similarity.ToReference(
            new MapScreenPoint(viewportBounds.X, viewportBounds.Y));
        var validBounds = (recognition.Map.Recognition
            .GetFloor(recognition.Result.Floor)
            ?? recognition.Map.Recognition.FirstFloor)
            .GetEffectiveValidMapBounds();
        var previousPlayer = updatesExistingLock
            ? SessionSnapshot.Player
            : null;
        var reprojectedPlayer = updatesExistingLock
            ? MapSessionRules.ReprojectPlayer(
                previousPlayer,
                similarity,
                validBounds)
            : null;
        var viewportOrigin = validBounds.ClampViewportOrigin(
            new MapViewportOrigin(
                viewportReferencePoint.X,
                viewportReferencePoint.Y),
            viewportBounds.Width / similarity.Scale,
            viewportBounds.Height / similarity.Scale);
        // Prepare and validate the next logical lock before any render work is
        // queued. An observation that cannot advance the reliable lock must
        // never become visible for even one dispatcher frame.
        var nextAlignmentSession = updatesExistingLock
            ? priorAlignmentSession!.AdvanceContinuousObservation(
                recognition.Map,
                recognition.Result,
                lockSnapshotAtObservation,
                continuousObservation!,
                Settings.SessionTuning.NativeScaleChangeRatio)
            : MapAlignmentSession.FromRecognition(
                recognition.Map,
                recognition.Result);
        var source = RecognitionSourceText(recognition.Result.Source)
            + (recognition.Result.WasForcedBestResult
                && recognition.Result.Source
                    != MapRecognitionSource.ReusedLastTransform
                    ? "（强制最优）"
                    : string.Empty);
        var fit = transform.IsExactFit ? "已贴合" : "未完全贴合";
        var errorLabel = recognition.Result.Source == MapRecognitionSource.StructureMatching
            ? $"平均边缘距离 {transform.MaximumResidualPixels:F1}px"
            : $"最大误差 {transform.MaximumResidualPixels:F1}px";
        var floorLabel = MapFloorRules.GetFloorDisplayName(
            recognition.Map,
            recognition.Result.Floor);
        var overlayStatus = new MapOverlayStatus(
            recognition.Result.WasForcedBestResult
                ? MapOverlayStatusLevel.Warning
                : transform.IsExactFit
                ? MapOverlayStatusLevel.Success
                : MapOverlayStatusLevel.Warning,
            recognition.Result.WasForcedBestResult
                ? "已强制呈现识别结果"
                : transform.IsExactFit
                    ? "识别图层已生效"
                    : "识别图层未完全贴合",
            $"{recognition.Map.DisplayName} · {floorLabel} · 置信度 {recognition.Result.Confidence:P0} · {source}",
            $"{transform.AlignmentMode.ToDisplayName()} · {errorLabel}"
            + (transform.UsedDegenerateAxisFallback ? " · 退化轴回退" : string.Empty));
        if (isManualSource)
            DwrGameWindowCaptureService.RestoreForegroundWindow(windowHandle);
        var committedWindowSignature = CreateWindowSignature(
            clientBounds,
            viewportBounds,
            windowHandle);
        void CommitLogicalLock()
        {
            var commitStart = Stopwatch.GetTimestamp();
            if (updatesExistingLock)
            {
                MapSessionSnapshot updatedSnapshot;
                lock (_sessionStateGate)
                {
                    updatedSnapshot = _mapOpenSession.UpdateLockedAlignment(
                        recognition.Map.Id,
                        recognition.Result.Floor,
                        ToLocationMethod(recognition.Result.Source),
                        viewportOrigin,
                        similarity,
                        recognition.Result.Confidence,
                        committedStableFrames,
                        "The locked alignment was updated by a trusted observation.");
                    updatedSnapshot =
                        _mapOpenSession.UpdatePlayer(reprojectedPlayer);
                }
                LogCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Info,
                    "Trusted alignment observation updated the locked transform.",
                    details: new()
                    {
                        ["mapId"] = recognition.Map.Id,
                        ["floor"] = recognition.Result.Floor,
                        ["source"] = recognition.Result.Source.ToString(),
                        ["confidence"] = recognition.Result.Confidence,
                        ["previousConfidence"] =
                            priorAlignmentSession!.LastConfidence,
                        ["consecutiveRejectionsBeforeUpdate"] =
                            priorAlignmentSession.ConsecutiveRejections,
                        ["observedStableFrames"] = observedStableFrames,
                        ["committedStableFrames"] = committedStableFrames,
                        ["playerReprojected"] = reprojectedPlayer is not null,
                        ["playerCleared"] = previousPlayer is not null
                            && reprojectedPlayer is null,
                        ["previousAlignmentRevision"] =
                            lockSnapshotAtObservation.AlignmentRevision,
                        ["alignmentRevision"] =
                            updatedSnapshot.AlignmentRevision
                    });
                _lastTrustedPlayerPoint = reprojectedPlayer?.ReferencePoint;
                _dispatcher.TryEnqueue(NotifyStateChanged);
            }
            else
            {
                TransitionSession(
                    MapSessionState.Locked,
                    mapId: recognition.Map.Id,
                    floor: recognition.Result.Floor,
                    locationMethod: ToLocationMethod(recognition.Result.Source),
                    viewportOrigin: viewportOrigin,
                    lockedTransform: similarity,
                    confidence: recognition.Result.Confidence,
                    stableCandidateFrames: committedStableFrames,
                    detail: "Background alignment is locked for this map-open session.");
            }
            _currentOverlayStatus = overlayStatus;
            _selectedMapLease.Bind(
                _matchSession.Snapshot,
                recognition.Map.Id);
            _alignmentSession = nextAlignmentSession;
            _alignmentTrackingMode = nextAlignmentSession.Mode;
            _lockedWindowSignature = committedWindowSignature;
            _candidateStability.Reset();
            diagnostics.SessionCommitMilliseconds =
                (Stopwatch.GetTimestamp() - commitStart)
                * 1000d / Stopwatch.Frequency;
            LastRecognition = recognition;
            LastDiagnostics = diagnostics;
            StatusMessage =
                $"已识别 {recognition.Map.DisplayName} · {floorLabel} · 置信度 {recognition.Result.Confidence:P0}"
                + $" · {source} · {transform.AlignmentMode.ToDisplayName()}"
                + $" · {errorLabel}（{fit}）"
                + $" · {diagnostics.ToStatusText()}";
        }
        var overlayTimer = new Stopwatch();
        var alignmentCommitGeneration = _alignmentCommitGuard.BeginCommit();
        var renderCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renderState = 0;
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            if (Interlocked.CompareExchange(ref renderState, -1, 0) == 0)
            {
                _alignmentCommitGuard.TryInvalidate(alignmentCommitGeneration);
                renderCompletion.TrySetCanceled(cancellationToken);
            }
        });
        if (!_dispatcher.TryEnqueue(() =>
        {
            if (Interlocked.CompareExchange(ref renderState, 1, 0) != 0)
                return;
            Exception? renderException = null;
            var committed = false;
            try
            {
                committed = _alignmentCommitGuard.TryCommit(
                    alignmentCommitGeneration,
                    () =>
                    {
                        if (continuousObservation is not null
                            && !continuousObservation.IsCurrent(
                                recognition.Map,
                                _alignmentSession,
                                SessionSnapshot))
                        {
                            throw new InvalidOperationException(
                                "The alignment lock changed before the continuous observation could commit.");
                        }
                        overlayTimer.Start();
                        _overlay.UpdateStatus(
                            overlayStatus,
                            clientBounds,
                            windowHandle,
                            Settings.ShowOverlayStatus,
                            showImmediately: false);
                        _overlay.LockBackground(
                            recognition,
                            viewportBounds,
                            clientBounds,
                            windowHandle,
                            Settings.ShowOverlayStatus,
                            preservePlayer: updatesExistingLock);
                        if (updatesExistingLock)
                            _overlay.UpdatePlayer(reprojectedPlayer);
                        if (Settings.PersistentMiniMapEnabled)
                        {
                            _overlay.SetPersistentMiniMapState(
                                recognition.FloorImagePath,
                                transform,
                                clientBounds,
                                windowHandle,
                                Settings.MiniMapScale,
                                floorLabel: MapFloorRules.GetFloorDisplayName(
                                    recognition.Map,
                                    recognition.Result.Floor));
                        }
                        CommitLogicalLock();
                    });
                if (!committed)
                {
                    renderCompletion.TrySetResult(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                // 焦点保护等异常可能触发 Hide() 留下 HasMap=true + IsVisible=false
                // 的撕裂状态；恢复可见性避免 Toggle 行为反转
                if (_overlay.HasMap && !_overlay.IsVisible)
                    _overlay.Show();
                LogCollector.Append(MapLogCategory.Overlay,
                    MapLogLevel.Warning,
                    $"叠加层渲染异常已恢复：{ex.Message}");
                _alignmentCommitGuard.TryInvalidate(alignmentCommitGeneration);
                renderException = ex;
            }
            finally
            {
                overlayTimer.Stop();
                diagnostics.OverlayMilliseconds = overlayTimer.Elapsed.TotalMilliseconds;
                LogCollector.Append(MapLogCategory.Overlay,
                    MapLogLevel.Info,
                    $"叠加层渲染完成 · {overlayTimer.Elapsed.TotalMilliseconds:F0}ms",
                    elapsedMs: overlayTimer.Elapsed.TotalMilliseconds);
                if (renderException is null && committed)
                    renderCompletion.TrySetResult(true);
                else if (renderException is not null)
                    renderCompletion.TrySetException(renderException);
            }
        }))
        {
            _alignmentCommitGuard.TryInvalidate(alignmentCommitGeneration);
            throw new InvalidOperationException(
                "The alignment render could not be queued on the UI dispatcher.");
        }
        var rendered = await renderCompletion.Task;
        if (!rendered)
        {
            LogSupersededAlignmentCommit(
                alignmentCommitGeneration,
                recognition);
            return false;
        }
        if (allowCalibrationUpdate && !updatesExistingLock)
        {
            try
            {
                await SaveAlignmentCalibrationAsync(
                    recognition,
                    committedWindowSignature);
            }
            catch (Exception exception)
            {
                // Calibration persistence happens after the visual/logical
                // lock has committed. A storage failure must not relabel that
                // valid lock as an alignment failure or clear its transform.
                LogCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Warning,
                    $"Alignment locked, but calibration persistence failed: {exception.Message}",
                    details: new()
                    {
                        ["mapId"] = recognition.Map.Id,
                        ["floor"] = recognition.Result.Floor,
                        ["exceptionType"] = exception.GetType().FullName
                    });
            }
        }
        return true;
    }

    private void LogSupersededAlignmentCommit(
        long generation,
        RuntimeMapRecognition recognition) =>
        LogCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            "A superseded alignment render was discarded before lock commit.",
            details: new()
            {
                ["generation"] = generation,
                ["mapId"] = recognition.Map.Id,
                ["floor"] = recognition.Result.Floor
            });

    private async Task RefreshSelectedMapReferenceAsync()
    {
        if (Settings.SelectedMapId is not { } selectedMapId)
        {
            SelectedMap = null;
            if (_alignmentSession is null)
                _alignmentTrackingMode = MapAlignmentTrackingMode.None;
            return;
        }

        var selected = _recognition.TryGetMap(selectedMapId);
        if (selected is not null)
        {
            if (_alignmentSession is { } session
                && (session.MapId != selected.Id
                    || session.MapUpdatedAt != selected.UpdatedAt))
            {
                InvalidateAlignment(MapAlignmentTrackingMode.Lost);
            }
            SelectedMap = selected;
            if (_alignmentSession is null)
                _alignmentTrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
            return;
        }

        var proposed = Settings.Clone();
        proposed.SelectedMapId = null;
        await _settingsRepository.SaveAsync(proposed);
        Settings = proposed;
        SelectedMap = null;
        InvalidateAlignment(MapAlignmentTrackingMode.None);
    }

    private void InvalidateAlignment(MapAlignmentTrackingMode mode)
    {
        _alignmentSession = null;
        _alignmentTrackingMode = mode;
        LastRecognition = null;
        LastDiagnostics = null;
        ClearOverlayMap();
        if (SessionSnapshot.IsLocked)
        {
            TransitionSession(
                MapSessionState.RecalibrationRequired,
                reason: MapRecalibrationReason.AlignmentLost,
                detail:
                    "The locked background was invalidated and must be located again.");
            _overlay.Hide();
        }
    }

    private void ClearOverlayMap()
    {
        _alignmentCommitGuard.Invalidate();
        _overlay.ClearMap();
    }

    private async Task<bool> TryRecognizeAutomaticFloorAsync(
        long inputTimestamp,
        MapScanDiagnostics diagnostics,
        CancellationToken cancellationToken,
        string failureTitle = "地图识别失败",
        MapFloorRecognitionIntent intent =
            MapFloorRecognitionIntent.AutomaticMapOpen)
    {
        if (Settings.SkipFloorRecognition)
        {
            CurrentFloorKey = SelectedMap is null
                ? "1f"
                : MapFloorRules.GetPrimaryFloorKey(SelectedMap);
            diagnostics.DetectedFloor = CurrentFloorKey;
            diagnostics.FloorConfidence = 1.0d;
            return true;
        }

        var requirePrimaryFloor =
            MapFloorRecognitionRules.RequiresConfirmedFirstFloor(intent);
        cancellationToken.ThrowIfCancellationRequested();
        MapFloorRecognitionResult result;
        using (var floorCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken))
        {
            if (!requirePrimaryFloor)
                floorCancellation.CancelAfter(TimeSpan.FromMilliseconds(350));
            try
            {
                result = await _floorRecognition.RecognizeAsync(
                    Settings.FloorDisplayRegion!,
                    inputTimestamp,
                    floorCancellation.Token,
                    Settings.FloorRecognitionTuning);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested
                    && !requirePrimaryFloor)
            {
                result = new MapFloorRecognitionResult
                {
                    FailureReason =
                        "显式识别的楼层诊断超过 350ms，已让扫描继续执行。"
                };
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        LastFloorRecognition = result;
        diagnostics.DetectedFloor = result.Floor;
        diagnostics.FloorConfidence = result.Confidence;
        diagnostics.FloorCaptureMilliseconds = result.CaptureMilliseconds;
        diagnostics.FloorAnalysisMilliseconds = result.AnalysisMilliseconds;
        diagnostics.FloorEndToEndMilliseconds = result.EndToEndMilliseconds;
        diagnostics.FloorQueueMilliseconds = result.QueueMilliseconds;
        diagnostics.FloorWorkerMilliseconds = result.WorkerMilliseconds;
        diagnostics.FloorRequestMilliseconds = result.RequestMilliseconds;
        diagnostics.FloorInputToResultMilliseconds = result.InputToResultMilliseconds;
        diagnostics.FloorRetryWaitMilliseconds = result.RetryWaitMilliseconds;
        diagnostics.FloorWorkerOverheadMilliseconds = result.WorkerOverheadMilliseconds;

        var route = MapFloorRecognitionRules.GetRoute(result);
        if (route == MapFloorRoute.Reject || result.Floor is not { } floor)
        {
            CurrentFloorKey = null;
            if (!requirePrimaryFloor)
                return true;
            // A transient floor-indicator failure must not destroy the map
            // identity or the last valid first-floor alignment session.
            ClearOverlayMap();
            PublishFailure(
                $"楼层识别失败：{result.FailureReason}",
                showOverlay: true,
                title: failureTitle);
            return false;
        }

        CurrentFloorKey = floor switch
        {
            "1f" when SelectedMap is not null =>
                MapFloorRules.GetFloorKeyAtPosition(SelectedMap, 1) ?? floor,
            "2f" when SelectedMap is not null =>
                MapFloorRules.GetFloorKeyAtPosition(SelectedMap, 2) ?? floor,
            _ => floor
        };
        diagnostics.DetectedFloor = CurrentFloorKey;
        if (route == MapFloorRoute.SecondFloorAlignment)
        {
            // The caller maps the raw 2F indicator to ordered floor position 2
            // and routes that exact FloorKey to gate-free structure alignment.
            return true;
        }
        return true;
    }

    /// <summary>
    /// A persistent mini-map can select the target floor, but it cannot prove
    /// that the game's native map is currently open. Verify the calibrated
    /// floor indicator before feeding a stable frame into structure matching;
    /// otherwise ordinary game-world edges can be mistaken for an oversized
    /// map query after the input toggle state becomes desynchronized.
    /// </summary>
    private async Task<bool> VerifyNativeMapIsOpenAsync(
        long inputTimestamp,
        MapScanDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (Settings.FloorDisplayRegion?.IsValid is not true)
            return false;

        using var presenceCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        presenceCancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        MapFloorRecognitionResult result;
        try
        {
            result = await _floorRecognition.RecognizeAsync(
                Settings.FloorDisplayRegion,
                inputTimestamp,
                presenceCancellation.Token,
                Settings.FloorRecognitionTuning);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            LogCollector.Append(
                MapLogCategory.FloorRecognition,
                MapLogLevel.Warning,
                "Persistent-floor map-open verification timed out after 500ms.");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        LastFloorRecognition = result;
        diagnostics.FloorCaptureMilliseconds = result.CaptureMilliseconds;
        diagnostics.FloorAnalysisMilliseconds = result.AnalysisMilliseconds;
        diagnostics.FloorEndToEndMilliseconds = result.EndToEndMilliseconds;
        diagnostics.FloorQueueMilliseconds = result.QueueMilliseconds;
        diagnostics.FloorWorkerMilliseconds = result.WorkerMilliseconds;
        diagnostics.FloorRequestMilliseconds = result.RequestMilliseconds;
        diagnostics.FloorInputToResultMilliseconds =
            result.InputToResultMilliseconds;
        diagnostics.FloorRetryWaitMilliseconds = result.RetryWaitMilliseconds;
        diagnostics.FloorWorkerOverheadMilliseconds =
            result.WorkerOverheadMilliseconds;

        var isOpen = MapFloorRecognitionRules.IsPublishableSuccess(result);
        LogCollector.Append(
            MapLogCategory.FloorRecognition,
            isOpen ? MapLogLevel.Info : MapLogLevel.Warning,
            isOpen
                ? "Persistent-floor map-open verification passed."
                : "Persistent-floor map-open verification rejected the frame.",
            details: new()
            {
                ["succeeded"] = result.Succeeded,
                ["floor"] = result.Floor,
                ["confidence"] = result.Confidence,
                ["failureReason"] = result.FailureReason,
                ["requestMs"] = result.RequestMilliseconds
            });
        return isOpen;
    }

    private bool TryValidateRecognitionStart(out string failureReason)
    {
        CheckIntegrityAndNotify();
        if (!Settings.IsEnabled)
        {
            failureReason = "解锁地图总开关已关闭。";
            return false;
        }
        if (!_matchSession.Snapshot.IsStarted)
        {
            failureReason = "请先通过外置控件层选择玩家序号并开始对局。";
            return false;
        }
        if (IntegrityStatus.RequiresElevation)
        {
            failureReason = IntegrityStatus.Message;
            return false;
        }
        if (!Settings.IsMapViewportCalibrated || Settings.MapViewportRegion is null)
        {
            failureReason = "请先在状态页校准游戏内地图区域。";
            return false;
        }
        if (!Settings.IsFloorDisplayCalibrated || Settings.FloorDisplayRegion is null)
        {
            failureReason = "请先在状态页校准楼层显示区。";
            return false;
        }
        failureReason = string.Empty;
        return true;
    }

    private void PublishFailure(
        string message,
        bool showOverlay,
        string title = "地图识别失败")
    {
        PublishStatus(
            message,
            new MapOverlayStatus(
                MapOverlayStatusLevel.Failure,
                title,
                message),
            showOverlay);
    }

    private void PublishAlignmentFailure(
        string message,
        bool showOverlay = true) =>
        PublishFailure(
            message,
            showOverlay,
            title: "地图对齐失败");

    private void PublishStatus(
        string message,
        MapOverlayStatus overlayStatus,
        bool showOverlay)
    {
        if (!_matchSession.Snapshot.IsStarted)
            return;
        StatusMessage = message;
        _currentOverlayStatus = overlayStatus;
        if (showOverlay)
            TryPublishOverlayStatus(overlayStatus, showImmediately: true);
        NotifyStateChanged();
    }

    private bool TryPublishOverlayStatus(
        MapOverlayStatus status,
        bool showImmediately)
    {
        if (!_captureService.TryGetForegroundClientBounds(
                out var currentBounds,
                out var currentHandle,
                out _))
        {
            return false;
        }
        _lastGameBounds = currentBounds;
        _lastGameWindowHandle = currentHandle;
        _overlay.UpdateStatus(
            status,
            _lastGameBounds,
            _lastGameWindowHandle,
            Settings.ShowOverlayStatus,
            showImmediately);
        return true;
    }

    private void RememberGameWindow(CapturedGameFrame frame)
    {
        _lastGameBounds = frame.ClientBounds;
        _lastGameWindowHandle = frame.WindowHandle;
    }

    private void FinishScan(Stopwatch total, MapScanDiagnostics diagnostics)
    {
        if (total.IsRunning)
            total.Stop();
        diagnostics.TotalMilliseconds = Math.Max(
            diagnostics.TotalMilliseconds,
            total.Elapsed.TotalMilliseconds);
        diagnostics.ReadyMapCount = _recognition.ReadyMapCount;
        diagnostics.TotalMapCount = _recognition.TotalMapCount;
        LastDiagnostics ??= diagnostics;
        IsScanning = false;
        _scanGate.Release();
        NotifyStateChanged();
    }

    private void CheckIntegrityAndNotify()
    {
        if (_disposed)
            return;
        var previousRequiresElevation = IntegrityStatus.RequiresElevation;
        IntegrityStatus = GameProcessIntegrityService.Check();
        if (IntegrityStatus.RequiresElevation)
        {
            StatusMessage = IntegrityStatus.Message;
            SetCurrentOverlayStatus(
                MapOverlayStatusLevel.Warning,
                "游戏内热键不可用",
                StatusMessage);
            if (!_elevationEventRaised)
            {
                _elevationEventRaised = true;
                ElevationRequiredDetected?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _elevationEventRaised = false;
        }
        if (previousRequiresElevation != IntegrityStatus.RequiresElevation)
            NotifyStateChanged();
    }

    private string BuildReadyStatus()
    {
        var calibration = Settings.IsMapViewportCalibrated
            ? "地图区域已校准"
            : "地图区域未校准";
        var floorCalibration = Settings.IsFloorDisplayCalibrated
            ? "楼层显示区已校准"
            : "楼层显示区未校准";
        var gameMapBinding = Settings.GameMapToggleBinding.IsConfigured
            ? $"游戏地图开关 {Settings.GameMapToggleBinding.DisplayName}"
            : "游戏地图开关未设置";
        var controlBinding = Settings.ControlPanelToggleBinding.IsConfigured
            ? $"外置控件层 {Settings.ControlPanelToggleBinding.DisplayName}"
            : "外置控件层按键未设置";
        var playerAssets = ArePlayerAssetsReady
            ? "玩家序号资源 4/4 就绪"
            : "玩家序号资源不完整";
        var selection = SelectedMap is { } selected
            ? $"当前选择 {selected.DisplayName}"
            : "尚未选择地图";
        return $"解锁地图已启动，{calibration}，{floorCalibration}，{gameMapBinding}，{controlBinding}，{playerAssets}，{selection}，"
            + $"地图 {_recognition.ReadyMapCount}/{_recognition.TotalMapCount} 就绪。";
    }

    private void SetCurrentOverlayStatus(
        MapOverlayStatusLevel level,
        string title,
        string message,
        string detail = "") =>
        _currentOverlayStatus = new MapOverlayStatus(level, title, message, detail);

    private static string RecognitionSourceText(MapRecognitionSource source) => source switch
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

    private static void MergeDiagnostics(
        MapScanDiagnostics target,
        MapScanDiagnostics source)
    {
        target.ReadyMapCount = source.ReadyMapCount;
        target.TotalMapCount = source.TotalMapCount;
        target.GateCandidateCount = source.GateCandidateCount;
        target.GateSearchMode = source.GateSearchMode;
        target.GateSearchStopReason = source.GateSearchStopReason;
        target.GateScalesEvaluated = source.GateScalesEvaluated;
        target.GateMatchTemplateCalls = source.GateMatchTemplateCalls;
        target.GateBudgetExceeded = source.GateBudgetExceeded;
        target.SearchStage = source.SearchStage;
        target.StructureAttempted = source.StructureAttempted;
        target.StructureAccepted = source.StructureAccepted;
        target.StructureFailureReason = source.StructureFailureReason;
        target.AuxiliaryAnchorMatchCount = source.AuxiliaryAnchorMatchCount;
        target.AuxiliaryTemplatesEvaluated =
            source.AuxiliaryTemplatesEvaluated;
        target.AuxiliaryAnchorMilliseconds =
            source.AuxiliaryAnchorMilliseconds;
        target.AuxiliaryConfidence = source.AuxiliaryConfidence;
        target.AuxiliaryUsedGlobalSearch =
            source.AuxiliaryUsedGlobalSearch;
        target.UsedSingleGateStructureFallback =
            source.UsedSingleGateStructureFallback;
        target.UsedForcedBestResult = source.UsedForcedBestResult;
        target.SingleGateFallbackReason = source.SingleGateFallbackReason;
        target.TrackingMode = source.TrackingMode;
        target.PreprocessMilliseconds = source.PreprocessMilliseconds;
        target.GateDetectionMilliseconds = source.GateDetectionMilliseconds;
        target.GeometryMilliseconds = source.GeometryMilliseconds;
        target.ConfirmationMilliseconds = source.ConfirmationMilliseconds;
        target.StructurePreprocessMilliseconds =
            source.StructurePreprocessMilliseconds;
        target.StructureSearchMilliseconds = source.StructureSearchMilliseconds;
        target.StructureRefineMilliseconds = source.StructureRefineMilliseconds;
        target.StructureBestScore = source.StructureBestScore;
        target.StructureSecondScore = source.StructureSecondScore;
        target.StructureCandidateMargin = source.StructureCandidateMargin;
        target.StructureGeometricFitQuality =
            source.StructureGeometricFitQuality;
        target.StructureEvidenceConfidence =
            source.StructureEvidenceConfidence;
        target.StructureGeometricLockConfidence =
            source.StructureGeometricLockConfidence;
        target.StructureLockConfidence =
            source.StructureLockConfidence;
        target.StructureLowEvidenceReason =
            source.StructureLowEvidenceReason;
        target.StructureHardGateFailure =
            source.StructureHardGateFailure;
        target.StructureRejectionReason = source.StructureRejectionReason;
        target.StructureDisposition = source.StructureDisposition;
        target.SkippedStructureValidation =
            source.SkippedStructureValidation;
        target.AlignmentEvidence = source.AlignmentEvidence;
        target.StructureCandidateCount = source.StructureCandidateCount;
        target.StructureFeatureMatchCount =
            source.StructureFeatureMatchCount;
        target.StructureFeatureInlierCount =
            source.StructureFeatureInlierCount;
        target.StructureFeatureConsensus =
            source.StructureFeatureConsensus;
        target.StructureEccConverged = source.StructureEccConverged;
        target.StructureEccCorrelation = source.StructureEccCorrelation;
    }

    private MapSessionSnapshot TransitionSession(
        MapSessionState state,
        Guid? mapId = null,
        string? floor = null,
        MapLocationMethod locationMethod = MapLocationMethod.None,
        MapRecalibrationReason reason = MapRecalibrationReason.None,
        MapViewportOrigin? viewportOrigin = null,
        MapSimilarityTransform? lockedTransform = null,
        MapPlayerState? player = null,
        double confidence = 0d,
        int stableCandidateFrames = 0,
        string? detail = null)
    {
        if (!_matchSession.Snapshot.IsStarted
            && state != MapSessionState.Closed)
        {
            return SessionSnapshot;
        }
        var previousSnapshot = SessionSnapshot;
        MapSessionSnapshot snapshot;
        lock (_sessionStateGate)
        {
            snapshot = _mapOpenSession.Transition(
                state,
                mapId,
                floor,
                locationMethod,
                reason,
                viewportOrigin,
                lockedTransform,
                player,
                confidence,
                stableCandidateFrames,
                detail);
        }
        LogCollector.Append(MapLogCategory.Session, MapLogLevel.Info,
            $"会话：{previousSnapshot.State} → {state}",
            details: new()
            {
                ["detail"] = detail ?? "",
                ["mapId"] = snapshot.MapId,
                ["floor"] = snapshot.Floor,
                ["confidence"] = snapshot.Confidence,
                ["stableCandidateFrames"] = snapshot.StableCandidateFrames,
                ["locationMethod"] = snapshot.LocationMethod.ToString(),
                ["recalibrationReason"] = snapshot.RecalibrationReason.ToString(),
                ["previousSessionVersion"] = previousSnapshot.Version,
                ["sessionVersion"] = snapshot.Version,
                ["previousAlignmentRevision"] =
                    previousSnapshot.AlignmentRevision,
                ["alignmentRevision"] = snapshot.AlignmentRevision
            });
        if (previousSnapshot.State == MapSessionState.Closed
            && snapshot.State == MapSessionState.OpeningDetected)
        {
            BeginRecognitionStatisticsAttempt();
        }
        if (snapshot.IsLocked)
            MarkRecognitionStatisticsAlignmentProduced();
        _dispatcher.TryEnqueue(NotifyStateChanged);
        return snapshot;
    }

    private void EnsureSessionCanLock(RuntimeMapRecognition recognition)
    {
        while (true)
        {
            var snapshot = SessionSnapshot;
            var state = snapshot.State;
            if (state == MapSessionState.Locked
                && (snapshot.MapId != recognition.Map.Id
                    || !string.Equals(
                        snapshot.Floor,
                        recognition.Result.Floor,
                        StringComparison.Ordinal)))
            {
                var mapChanged = snapshot.MapId != recognition.Map.Id;
                TransitionSession(
                    MapSessionState.RecalibrationRequired,
                    mapId: recognition.Map.Id,
                    floor: recognition.Result.Floor,
                    reason: mapChanged
                        ? MapRecalibrationReason.MapIdentityChanged
                        : MapRecalibrationReason.FloorChanged,
                    detail:
                        mapChanged
                            ? "The recognized map ID changed; the previous background cannot be reused."
                            : "The recognized floor changed; the previous floor lock cannot be reused.");
                continue;
            }
            if (state is MapSessionState.FineLocating
                or MapSessionState.Confirming
                or MapSessionState.Locked)
            {
                return;
            }

            switch (state)
            {
                case MapSessionState.Closed:
                    TransitionSession(
                        MapSessionState.OpeningDetected,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor,
                        reason: MapRecalibrationReason.MapReopened,
                        detail: "A map-open session was established by recognition.");
                    break;
                case MapSessionState.OpeningDetected:
                    TransitionSession(
                        MapSessionState.WaitingForStableFrames,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor);
                    break;
                case MapSessionState.WaitingForStableFrames:
                    TransitionSession(
                        MapSessionState.IdentifyingMap,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor);
                    break;
                case MapSessionState.IdentifyingMap:
                    TransitionSession(
                        MapSessionState.CoarseLocating,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor);
                    break;
                case MapSessionState.CoarseLocating:
                    TransitionSession(
                        MapSessionState.FineLocating,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor);
                    break;
                case MapSessionState.LowConfidence:
                case MapSessionState.RecalibrationRequired:
                    TransitionSession(
                        MapSessionState.CoarseLocating,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor);
                    break;
                case MapSessionState.Lost:
                    TransitionSession(
                        MapSessionState.RecalibrationRequired,
                        mapId: recognition.Map.Id,
                        floor: recognition.Result.Floor,
                        reason: MapRecalibrationReason.AlignmentLost);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Cannot lock a map background from session state {state}.");
            }
        }
    }

    private static MapLocationMethod ToLocationMethod(
        MapRecognitionSource source) =>
        source switch
        {
            MapRecognitionSource.SelectedMapGatePair =>
                MapLocationMethod.DualAnchor,
            MapRecognitionSource.SingleGateTracking =>
                MapLocationMethod.SingleAnchor,
            MapRecognitionSource.AuxiliaryAnchorTracking =>
                MapLocationMethod.AuxiliaryAnchor,
            MapRecognitionSource.StructureMatching =>
                MapLocationMethod.StructureTranslation,
            MapRecognitionSource.ManualGateSelection
                or MapRecognitionSource.UserConfirmed =>
                MapLocationMethod.Manual,
            _ => MapLocationMethod.None
        };

    private static MapWindowSignature CreateWindowSignature(
        CapturedGameFrame frame) =>
        CreateWindowSignature(
            frame.ClientBounds,
            frame.ViewportBounds,
            frame.WindowHandle);

    private static MapWindowSignature CreateWindowSignature(
        MapScreenRect clientBounds,
        MapScreenRect viewportBounds,
        IntPtr windowHandle) =>
        new()
        {
            WindowHandle = windowHandle.ToInt64(),
            ClientX = (int)Math.Round(clientBounds.X),
            ClientY = (int)Math.Round(clientBounds.Y),
            ClientWidth = (int)Math.Round(clientBounds.Width),
            ClientHeight = (int)Math.Round(clientBounds.Height),
            ViewportX = (int)Math.Round(viewportBounds.X),
            ViewportY = (int)Math.Round(viewportBounds.Y),
            ViewportWidth = (int)Math.Round(viewportBounds.Width),
            ViewportHeight = (int)Math.Round(viewportBounds.Height),
            Dpi = DwrGameWindowCaptureService.GetWindowDpi(windowHandle)
        };

    private MapAlignmentSession? CreateAlignmentSeed(
        MapRecord map,
        CapturedGameFrame frame,
        MapWindowSignature signature)
    {
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var calibration = Settings.AlignmentCalibrations
            .Where(candidate => candidate.Matches(
                map.Id,
                map.UpdatedAt,
                signature,
                primaryFloorKey))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefault();
        if (calibration is null)
            return null;

        var primaryProfile = MapFloorRules.GetFloorProfile(map, primaryFloorKey)
            ?? map.Recognition.FirstFloor;
        var bounds = primaryProfile.GetEffectiveValidMapBounds();
        var origin = _lastTrustedPlayerPoint is { } player
            ? MapSessionRules.PredictViewportOrigin(
                player,
                frame.ViewportBounds.Width,
                frame.ViewportBounds.Height,
                calibration.UniformScale,
                bounds)
            : bounds.ClampViewportOrigin(
                new MapViewportOrigin(bounds.X, bounds.Y),
                frame.ViewportBounds.Width / calibration.UniformScale,
                frame.ViewportBounds.Height / calibration.UniformScale);
        var untranslated = new MapSimilarityTransform
        {
            Scale = calibration.UniformScale,
            RotationDegrees = calibration.RotationDegrees
        };
        var projectedOrigin = untranslated.ToScreen(origin.AsPoint());
        var transform = new MapSimilarityTransform
        {
            Scale = calibration.UniformScale,
            RotationDegrees = calibration.RotationDegrees,
            TranslationX = frame.ViewportBounds.X - projectedOrigin.X,
            TranslationY = frame.ViewportBounds.Y - projectedOrigin.Y
        }.ToOverlayTransform(
            calibration.ReferenceWidth,
            calibration.ReferenceHeight);
        return new MapAlignmentSession
        {
            MapId = map.Id,
            MapUpdatedAt = map.UpdatedAt,
            FloorKey = primaryFloorKey,
            LockedTransform = transform,
            BaselineGateScale = calibration.UniformScale,
            LastConfidence = calibration.Confidence,
            LastSuccessfulAt = calibration.UpdatedAt,
            HasGatePairLock = true,
            Mode = MapAlignmentTrackingMode.NeedsGatePair
        };
    }

    private MapViewportOrigin? PredictViewportOrigin(
        MapRecord map,
        CapturedGameFrame frame,
        MapAlignmentSession? seed,
        string? floor = null)
    {
        if (seed is null || _lastTrustedPlayerPoint is not { } player)
            return null;
        return MapSessionRules.PredictViewportOrigin(
            player,
            frame.ViewportBounds.Width,
            frame.ViewportBounds.Height,
            seed.LockedTransform.ScaleX,
            (MapFloorRules.GetFloorProfile(
                map,
                floor ?? MapFloorRules.GetPrimaryFloorKey(map))
                ?? map.Recognition.FirstFloor)
                .GetEffectiveValidMapBounds());
    }

    private IReadOnlyList<NormalizedRectangle> BuildLiveIgnoreRegions(
        CapturedGameFrame frame)
    {
        var regions = Settings.SessionTuning.ViewportIgnoreRegions
            .Where(region => region?.IsValid is true)
            .Select(region => region.Clone())
            .ToList();
        var player = SessionSnapshot.Player;
        if (player?.IsTrustedAt(
                Settings.PlayerTrackingTuning.MinimumConfidence) is not true
            || frame.Image.Empty())
        {
            return regions;
        }

        var markerWidth = player.MarkerWidth + 12d;
        var markerHeight = player.MarkerHeight + 12d;
        var left = Math.Clamp(
            (player.ViewportPoint.X - (markerWidth / 2d))
                / frame.Image.Width,
            0d,
            1d);
        var top = Math.Clamp(
            (player.ViewportPoint.Y - (markerHeight / 2d))
                / frame.Image.Height,
            0d,
            1d);
        var right = Math.Clamp(
            (player.ViewportPoint.X + (markerWidth / 2d))
                / frame.Image.Width,
            left,
            1d);
        var bottom = Math.Clamp(
            (player.ViewportPoint.Y + (markerHeight / 2d))
                / frame.Image.Height,
            top,
            1d);
        var playerRegion = new NormalizedRectangle
        {
            X = left,
            Y = top,
            Width = right - left,
            Height = bottom - top
        };
        if (playerRegion.IsValid)
            regions.Add(playerRegion);
        return regions;
    }

    private AlignmentSearchContext BuildAlignmentSearchContext(
        MapAlignmentSession? session,
        MapRecognitionTuning tuning,
        bool allowFullSearchUpgrade = true)
    {
        var hasReliableSession = session is not null
            && session.HasGatePairLock
            && session.BaselineGateScale > 0d;

        GateSearchContext gateContext;
        if (hasReliableSession)
        {
            // 门模板匹配 scale 与叠加层变换 scale 不同：
            // BaselineGateScale 是参考地图像素→屏幕像素的变换 scale（通常 0.5-1.5），
            // 而 LockedScale 需要的是门图标的模板匹配 scale（通常 0.15-0.4）。
            // GateTemplateScale 优先（来自 LockedGateEvidence），
            // 否则使用检测器内部记忆的 _warmScale（由上次成功检测设置）。
            var gateTemplateScale = session!.GateTemplateScale
                ?? _recognition.LastGateTemplateScale;
            if (gateTemplateScale is { } gts && gts > 0d)
            {
                gateContext = new GateSearchContext
                {
                    Mode = GateSearchMode.LockedScale,
                    LockedScale = gts,
                };
            }
            else
            {
                // GateTemplateScale 不可用时回退到 WarmScaleSearch
                gateContext = new GateSearchContext
                {
                    Mode = GateSearchMode.WarmScaleSearch,
                    WarmScale = session.BaselineGateScale,
                    AllowSingleGateEarlyExit = true,
                    SingleGateScoreThreshold =
                        GateTemplateRules.EarlyExitScoreThreshold,
                    SingleGateScaleTolerance =
                        GateTemplateRules.SingleGateScaleTolerance,
                    AmbiguityScoreGap = GateTemplateRules.SingleGateAmbiguityGap,
                };
                if (tuning.WarmGateSearchBudgetMs > 0)
                    gateContext.TimeBudgetMilliseconds =
                        tuning.WarmGateSearchBudgetMs;
            }
        }
        else
        {
            gateContext = new GateSearchContext
            {
                Mode = GateSearchMode.FullSearch,
            };
        }

        return new AlignmentSearchContext
        {
            GateSearch = gateContext,
            UseRestrictedStructureFallback = false,
            RequireCurrentFrameEvidence = false,
            AllowFullSearchUpgrade = allowFullSearchUpgrade,
        };
    }

    private async Task<MapRecognitionAttempt> ValidateInitialRecognitionAsync(
        CapturedGameFrame frame,
        RuntimeMapRecognition initial,
        CancellationToken cancellationToken)
    {
        // Dual-gate geometry already confirmed the map identity in the
        // initial scan (Recognize / manual). Structure registration is a
        // tracking-phase concern; running it here can reject a correct
        // result due to insufficient explored map area. Return the initial
        // recognition directly.
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.FromResult(new MapRecognitionAttempt
        {
            Recognition = initial
        });
    }

    private async Task<RuntimeMapRecognition> EnforceLockConfidenceAsync(
        RuntimeMapRecognition recognition,
        CancellationToken cancellationToken,
        MapRecognitionAttempt? previousAttempt = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (recognition.Result.Confidence
            < Settings.SessionTuning.MediumConfidence)
        {
            throw new MapAlignmentConfirmationException(
                $"Alignment confidence {recognition.Result.Confidence:P1} "
                + $"is below the safe lock threshold "
                + $"{Settings.SessionTuning.MediumConfidence:P1}.");
        }

        // Scan already validated map identity via dual-gate geometry.
        // Multi-frame confirmation with structure registration is a
        // tracking concern, not a scan concern — accept immediately.
        return await Task.FromResult(recognition);
    }

    private async Task<MapRecognitionAttempt?> TryRecheckMapIdentityAsync(
        CapturedGameFrame frame,
        Guid selectedMapId,
        MapStructureRegistrationTuning structureTuning,
        CancellationToken cancellationToken)
    {
        // A full-catalog check is permitted only when the selected-map
        // attempt saw a dual-anchor frame. Single/no-anchor paths never call
        // this helper.
        var global = await Task.Run(
            () => _recognition.Recognize(
                frame,
                MapOverlayAlignmentMode.Uniform,
                Settings.RecognitionTuning.Clone(),
                mapClass: _matchSession.Snapshot.MapClass),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (global.Recognition is null
            || global.Recognition.Map.Id == selectedMapId
            || global.Recognition.Result.Confidence
                < Settings.SessionTuning.MediumConfidence)
        {
            return null;
        }

        var seed = MapAlignmentSession.FromRecognition(
            global.Recognition.Map,
            global.Recognition.Result);
        var predicted = PredictViewportOrigin(
            global.Recognition.Map,
            frame,
            seed);
        var searchCtx = BuildAlignmentSearchContext(
            seed,
            Settings.RecognitionTuning.Clone());
        var confirmed = await Task.Run(
            () => _recognition.AlignSelected(
                frame,
                global.Recognition.Map.Id,
                seed,
                MapOverlayAlignmentMode.Uniform,
                Settings.RecognitionTuning.Clone(),
                structureTuning,
                _lastTrustedPlayerPoint,
                predicted,
                BuildLiveIgnoreRegions(frame),
                alignmentSearchContext: searchCtx,
                nativeScaleChangeRatio:
                    Settings.SessionTuning.NativeScaleChangeRatio,
                mapClass: _matchSession.Snapshot.MapClass),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return confirmed.Recognition is null
            ? null
            : confirmed;
    }

    /// <summary>
    /// Returns the exact floor whose image is currently shown by the persistent
    /// mini-map. Merely remembering a previously detected floor is not a display
    /// lock; when no mini-map image is visible callers may run floor recognition.
    /// </summary>
    private bool TryGetDisplayedMiniMapFloorKey(out string floorKey)
    {
        floorKey = string.Empty;
        // SelectedMap is null（如对局刚开始、尚未识别）时，LastRecognition
        // 仍然持有上一局的识别结果，不能把它当作当前地图的楼层信息使用。
        if (!Settings.PersistentMiniMapEnabled
            || LastRecognition is not { } displayed
            || SelectedMap is null
            || displayed.Map.Id != SelectedMap.Id)
        {
            return false;
        }

        floorKey = _manualFloorOverrideKey ?? displayed.Result.Floor;
        return !string.IsNullOrWhiteSpace(floorKey);
    }

    /// <summary>
    /// Aligns one selected non-primary floor without gates.  Only the primary
    /// scale is used for seeding; target scale and translation are solved from
    /// the exact target reference image.
    /// </summary>
    private async Task RunFloorWithoutGatesAlignmentAsync(
        CancellationToken cancellationToken,
        long inputTimestamp,
        MapGameToggleTransition openTransition,
        MapScanDiagnostics diagnostics,
        StableViewportCaptureResult? stableFrames,
        string floorKey)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_gameMapToggleState.IsCurrent(openTransition))
            return;
        CurrentFloorKey = floorKey;
        if (Settings.SelectedMapId is not { } selectedMapId
            || SelectedMap is null)
        {
            _alignmentTrackingMode = MapAlignmentTrackingMode.None;
            PublishAlignmentFailure(
                $"已确认楼层 {floorKey}，但尚未选择地图；请先在主层识别地图。",
                showOverlay: true);
            return;
        }
        if (!_selectedMapLease.IsCurrent(
                _matchSession.Snapshot,
                selectedMapId))
        {
            _alignmentTrackingMode = MapAlignmentTrackingMode.None;
            PublishAlignmentFailure(
                "The selected map belongs to an earlier match. Run the configured initial scan for this match before floor alignment.",
                showOverlay: true);
            return;
        }
        TransitionSession(
            MapSessionState.IdentifyingMap,
            mapId: selectedMapId,
            floor: floorKey,
            detail: $"已确认楼层 {floorKey}，正在确认当前地图 ID。");
        var floorLabel = MapFloorRules.GetFloorDisplayName(SelectedMap, floorKey);
        StatusMessage =
            $"已识别为 {floorLabel}，正在刷新 {SelectedMap.DisplayName} 的无门对齐……";
        NotifyStateChanged();

        CapturedGameFrame? frame;
        bool ownsFrame;
        var captureStage = Stopwatch.StartNew();
        if (stableFrames?.PrimaryFrame is { } primary)
        {
            // Reuse the stability-detected primary frame — no new capture.
            diagnostics.AlignmentCaptureMilliseconds = 0d;
            frame = primary;
            ownsFrame = false;
        }
        else
        {
            if (!_captureService.TryCaptureViewport(
                    Settings.MapViewportRegion!,
                    out frame,
                    out var captureFailure)
                || frame is null)
            {
                captureStage.Stop();
                diagnostics.CaptureMilliseconds = captureStage.Elapsed.TotalMilliseconds;
                PublishAlignmentFailure(captureFailure);
                return;
            }
            diagnostics.AlignmentCaptureMilliseconds = captureStage.Elapsed.TotalMilliseconds;
            diagnostics.CaptureMilliseconds = diagnostics.AlignmentCaptureMilliseconds;
            ownsFrame = true;
        }
        captureStage.Stop();
        RememberGameWindow(frame);

        using (var frameDispose = ownsFrame ? frame : null)
        {
            var previousUpdatedAt = SelectedMap.UpdatedAt;
            await _recognition.RefreshCacheAsync();
            await RefreshSelectedMapReferenceAsync();
            if (SelectedMap is null
                || Settings.SelectedMapId != selectedMapId)
            {
                _alignmentTrackingMode = MapAlignmentTrackingMode.None;
                PublishAlignmentFailure(
                    "先前选择的地图已不存在；只清除了失效的地图序号，没有修改其他地图数据。",
                    showOverlay: true);
                return;
            }
            if (SelectedMap.UpdatedAt != previousUpdatedAt)
                InvalidateAlignment(MapAlignmentTrackingMode.Lost);

            var scaleSeed = ResolveFloorScaleSeed(SelectedMap, floorKey, frame);
            if (scaleSeed is null)
            {
                _alignmentTrackingMode = MapAlignmentTrackingMode.NeedsGatePair;
                PublishAlignmentFailure(
                    $"{floorLabel} 对齐需要先完成主层双门对齐。",
                    showOverlay: true);
                return;
            }
            if (!File.Exists(_mapRepository.GetFloorOverlayPath(SelectedMap, floorKey)))
            {
                _alignmentTrackingMode = MapAlignmentTrackingMode.None;
                PublishAlignmentFailure(
                    $"{floorLabel} 叠加图缺失，请重新生成地图资源。",
                    showOverlay: true);
                return;
            }

            var hasTrustedTargetTransform = _alignmentSession is { } targetSession
                && string.Equals(targetSession.FloorKey, floorKey, StringComparison.Ordinal);
            var predictedOrigin = hasTrustedTargetTransform
                ? PredictViewportOrigin(SelectedMap, frame, _alignmentSession, floorKey)
                : null;
            var playerPrior = hasTrustedTargetTransform
                ? _lastTrustedPlayerPoint
                : null;
            var tuning = Settings.RecognitionTuning.Clone();
            var structureTuning =
                Settings.StructureRegistrationTuning.Clone();
            var scaleSearch = MapFloorScaleSearchPolicy.GetRadii(
                scaleSeed.IsCalibrated);
            structureTuning.ScaleSearchRadius = scaleSearch.InitialRadius;
            structureTuning.TrackingScaleSearchRadius = 0.04d;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpenPipelineCurrent(openTransition))
                return;
            TransitionSession(
                MapSessionState.CoarseLocating,
                mapId: selectedMapId,
                floor: floorKey,
                detail: $"{floorLabel} 无门结构配准：使用专属 scale seed，整图搜索平移。");

            var expandedSearchUsed = false;
            var attempt = await Task.Run(
                () => _recognition.AlignFloorWithoutGates(
                    frame,
                    selectedMapId,
                    floorKey,
                    scaleSeed.Transform,
                    Settings.OverlayAlignmentMode,
                    tuning,
                    structureTuning,
                    playerPrior,
                    predictedOrigin,
                    BuildLiveIgnoreRegions(frame),
                    useProjectedBoundaryMask: hasTrustedTargetTransform),
                cancellationToken).ConfigureAwait(false);
            if (attempt.Recognition is null)
            {
                expandedSearchUsed = true;
                RecordResearchAttempt(
                    SelectedMap,
                    floorKey,
                    frame,
                    attempt,
                    _manualFloorOverrideKey is not null
                        ? "manual-mini-map"
                        : Settings.PersistentMiniMapEnabled
                            ? "displayed-mini-map"
                            : "automatic-indicator",
                    scaleSeed,
                    [structureTuning.ScaleSearchRadius]);
                var expandedTuning = structureTuning.Clone();
                expandedTuning.ScaleSearchRadius = scaleSearch.ExpandedRadius;
                attempt = await Task.Run(
                    () => _recognition.AlignFloorWithoutGates(
                        frame,
                        selectedMapId,
                        floorKey,
                        scaleSeed.Transform,
                        Settings.OverlayAlignmentMode,
                        tuning,
                        expandedTuning,
                        playerPrior,
                        predictedOrigin,
                        BuildLiveIgnoreRegions(frame),
                        useProjectedBoundaryMask: hasTrustedTargetTransform),
                    cancellationToken).ConfigureAwait(false);
                if (attempt.Recognition is null)
                {
                    RecordResearchAttempt(
                        SelectedMap,
                        floorKey,
                        frame,
                        attempt,
                        _manualFloorOverrideKey is not null
                            ? "manual-mini-map"
                            : Settings.PersistentMiniMapEnabled
                                ? "displayed-mini-map"
                                : "automatic-indicator",
                        scaleSeed,
                        [structureTuning.ScaleSearchRadius, expandedTuning.ScaleSearchRadius]);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpenPipelineCurrent(openTransition))
                return;
            MergeDiagnostics(diagnostics, attempt.Diagnostics);
            if (attempt.Recognition is null)
            {
                TransitionSession(
                    MapSessionState.LowConfidence,
                    mapId: selectedMapId,
                    floor: floorKey,
                    confidence: GetAttemptConfidence(attempt),
                    detail: FormatAlignmentFailure(
                        attempt,
                        structureTuning));
                PublishAlignmentFailure(
                    FormatAlignmentFailure(attempt, structureTuning));
                return;
            }

            TransitionSession(
                MapSessionState.FineLocating,
                mapId: selectedMapId,
                floor: floorKey,
                locationMethod: ToLocationMethod(
                    attempt.Recognition.Result.Source),
                confidence: attempt.Recognition.Result.Confidence,
                detail: $"{floorLabel} 结构配准完成，正在校验置信度。");
            var acceptedRecognition = attempt.Recognition;
            if (acceptedRecognition.Result.Confidence
                < Settings.SessionTuning.MediumConfidence)
            {
                TransitionSession(
                    MapSessionState.LowConfidence,
                    mapId: selectedMapId,
                    floor: floorKey,
                    confidence: acceptedRecognition.Result.Confidence,
                    detail: $"{floorLabel} 配准置信度低于会话锁定阈值。");
                PublishAlignmentFailure(
                    $"本次 {floorLabel} 配准置信度 "
                    + $"{acceptedRecognition.Result.Confidence:P1} "
                    + $"低于安全阈值 "
                    + $"{Settings.SessionTuning.MediumConfidence:P1}。");
                return;
            }
            var stableConfirmed = false;
            var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(SelectedMap);
            var shouldConfirmForCalibration =
                !scaleSeed.IsCalibrated
                && !string.Equals(primaryFloorKey, floorKey, StringComparison.Ordinal);
            if (acceptedRecognition.Result.Confidence
                    < Settings.SessionTuning.HighConfidence
                && (!Settings.SessionTuning.SkipStabilityConfirmation
                    || shouldConfirmForCalibration))
            {
                TransitionSession(
                    MapSessionState.Confirming,
                    mapId: selectedMapId,
                    floor: floorKey,
                    locationMethod: ToLocationMethod(
                        acceptedRecognition.Result.Source),
                    confidence: acceptedRecognition.Result.Confidence,
                    stableCandidateFrames: 1,
                    detail: $"{floorLabel} 中等置信度结果正在等待连续帧确认。");
                acceptedRecognition =
                    await RunFloorWithoutGatesConfirmationAsync(
                        acceptedRecognition,
                        selectedMapId,
                        floorKey,
                        structureTuning,
                        cancellationToken,
                        preparedConfirmationFrame: stableFrames?.ConfirmationFrame)
                    ?? throw new MapAlignmentConfirmationException(
                        $"{floorLabel} 中等置信度候选未通过连续帧确认。");
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsOpenPipelineCurrent(openTransition))
                    return;
                stableConfirmed = true;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOpenPipelineCurrent(openTransition))
                return;
            var shouldLearnCalibration =
                acceptedRecognition.Result.Confidence
                    >= Settings.SessionTuning.HighConfidence
                || stableConfirmed;
            if (!await ApplyRecognitionAsync(
                acceptedRecognition,
                frame.ClientBounds,
                frame.ViewportBounds,
                frame.WindowHandle,
                diagnostics,
                cancellationToken,
                allowCalibrationUpdate: shouldLearnCalibration))
            {
                return;
            }
            string? calibrationRejectionReason = null;
            if (shouldLearnCalibration)
            {
                calibrationRejectionReason = await SaveFloorScaleCalibrationAsync(
                    acceptedRecognition,
                    scaleSeed.PrimaryScale);
            }
            RecordResearchAttempt(
                SelectedMap,
                floorKey,
                frame,
                new MapRecognitionAttempt
                {
                    Recognition = acceptedRecognition,
                    StructureResult = attempt.StructureResult,
                    Diagnostics = attempt.Diagnostics
                },
                _manualFloorOverrideKey is not null
                    ? "manual-mini-map"
                    : Settings.PersistentMiniMapEnabled
                        ? "displayed-mini-map"
                        : "automatic-indicator",
                scaleSeed,
                expandedSearchUsed
                    ? [structureTuning.ScaleSearchRadius,
                        scaleSearch.ExpandedRadius]
                    : [structureTuning.ScaleSearchRadius],
                stableConfirmationFrames: stableConfirmed
                    ? Settings.SessionTuning.MediumConfidenceFrames
                    : 0,
                stableConfirmationRequiredFrames: stableConfirmed
                    ? Settings.SessionTuning.MediumConfidenceFrames
                    : 0,
                calibrationUpdated: shouldLearnCalibration
                    && calibrationRejectionReason is null,
                calibrationRejectionReason: calibrationRejectionReason);
            LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
                $"开图对齐完成（{floorLabel}）：{acceptedRecognition.Map.DisplayName} · 置信度 {acceptedRecognition.Result.Confidence:P0}");
        }
    }

    /// <summary>Resolves a scale-only seed for one exact non-primary floor.</summary>
    private FloorScaleSeed? ResolveFloorScaleSeed(
        MapRecord map,
        string floorKey,
        CapturedGameFrame frame)
    {
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var primaryProfile = MapFloorRules.GetFloorProfile(map, primaryFloorKey);
        var targetProfile = MapFloorRules.GetFloorProfile(map, floorKey);
        if (primaryProfile is null || targetProfile is null)
            return null;

        var signature = CreateWindowSignature(frame);
        double? primaryScale = null;

        // 标定数据（双门对齐过程中学到的精确缩放值）优先于
        // LastRecognition。侧门扫描等非双门路径的成功结果也会设置
        // LastRecognition，但其 ScaleX 来自结构匹配而非门标定，
        // 精度不足以作为非主层对齐的基线缩放，可能导致所有候选缩放
        // 视口超界（QueryLargerThanReference）。
        primaryScale = Settings.AlignmentCalibrations
            .Where(candidate => candidate.Matches(
                map.Id,
                map.UpdatedAt,
                signature,
                primaryFloorKey))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.UpdatedAt)
            .Select(candidate => (double?)candidate.UniformScale)
            .FirstOrDefault();

        if (primaryScale is null
            && LastRecognition?.Map.Id == map.Id
            && LastRecognition.Map.UpdatedAt == map.UpdatedAt
            && string.Equals(
                LastRecognition.Result.Floor,
                primaryFloorKey,
                StringComparison.Ordinal)
            && LastRecognition.Result.OverlayTransform is { ScaleX: > 0.05d } live)
        {
            primaryScale = live.ScaleX;
        }
        if (primaryScale is not { } trustedPrimaryScale
            || !double.IsFinite(trustedPrimaryScale)
            || trustedPrimaryScale <= 0.05d)
        {
            return null;
        }

        var targetAbsolute = Settings.AlignmentCalibrations
            .Where(candidate => candidate.Matches(
                map.Id,
                map.UpdatedAt,
                signature,
                floorKey))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefault();
        if (targetAbsolute is { UniformScale: > 0.05d })
        {
            return new FloorScaleSeed(
                CreateScaleOnlyTransform(targetAbsolute.UniformScale, targetProfile),
                trustedPrimaryScale,
                true,
                "target-window-calibration");
        }

        var learned = Settings.FloorScaleCalibrations
            .Where(candidate => candidate.Matches(
                map.Id,
                map.UpdatedAt,
                primaryFloorKey,
                floorKey))
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefault();
        if (learned is { MedianRatio: > 0d } && double.IsFinite(learned.MedianRatio))
        {
            return new FloorScaleSeed(
                CreateScaleOnlyTransform(
                    trustedPrimaryScale * learned.MedianRatio,
                    targetProfile),
                trustedPrimaryScale,
                true,
                "floor-specific-ratio");
        }

        if (primaryProfile.RecognitionPixelWidth <= 0
            || primaryProfile.RecognitionPixelHeight <= 0
            || targetProfile.RecognitionPixelWidth <= 0
            || targetProfile.RecognitionPixelHeight <= 0)
        {
            return null;
        }
        var referenceRatio = MapFloorScaleSeedRules.ResolveReferenceScaleRatio(
            primaryProfile,
            targetProfile,
            out var usedDimensionRatio);
        return new FloorScaleSeed(
            CreateScaleOnlyTransform(
                trustedPrimaryScale * referenceRatio,
                targetProfile),
            trustedPrimaryScale,
            false,
            usedDimensionRatio
                ? "reference-dimension-ratio"
                : "primary-scale-fallback");
    }

    public async Task SetCollectAlignmentResearchDataAsync(bool enabled)
    {
        using var operation = EnterApiOperation();
        await InitializeAsync();
        if (Settings.CollectAlignmentResearchData == enabled)
            return;
        var proposed = Settings.Clone();
        proposed.CollectAlignmentResearchData = enabled;
        await _settingsRepository.SaveAsync(proposed);
        Settings = proposed;
        try
        {
            await _researchCollector.SetEnabledAsync(enabled);
        }
        catch (Exception exception)
        {
            LogCollector.Append(
                MapLogCategory.System,
                MapLogLevel.Warning,
                $"Alignment research collection state change failed: {exception.Message}");
        }
        NotifyStateChanged();
    }

    private static MapOverlayTransform CreateScaleOnlyTransform(
        double scale,
        FloorRecognitionProfile profile)
    {
        var centerX = profile.RecognitionPixelWidth / 2d;
        var centerY = profile.RecognitionPixelHeight / 2d;
        return new MapOverlayTransform
        {
            ScaleX = scale,
            ScaleY = scale,
            OffsetX = 0d,
            OffsetY = 0d,
            ReferenceCenterX = centerX,
            ReferenceCenterY = centerY,
            ScreenCenterX = centerX * scale,
            ScreenCenterY = centerY * scale,
            ReferenceWidth = profile.RecognitionPixelWidth,
            ReferenceHeight = profile.RecognitionPixelHeight,
            OrientationDegrees = profile.OrientationDegrees,
            AlignmentMode = MapOverlayAlignmentMode.Uniform
        };
    }

    private async Task<RuntimeMapRecognition?> RunFloorWithoutGatesConfirmationAsync(
        RuntimeMapRecognition initial,
        Guid selectedMapId,
        string floorKey,
        MapStructureRegistrationTuning structureTuning,
        CancellationToken cancellationToken,
        CapturedGameFrame? preparedConfirmationFrame = null)
    {
        var confirmTimer = Stopwatch.StartNew();
        var floorLabel = MapFloorRules.GetFloorDisplayName(initial.Map, floorKey);
        var extraFrames = 0;
        double confirmationDelayMs = 0d;
        double confirmationCaptureMs = 0d;
        double confirmationComputeMs = 0d;
        _candidateStability.Reset();
        var latest = initial;
        var transform = initial.Result.OverlayTransform;
        if (transform is null)
            return null;
        _candidateStability.Observe(
            MapSimilarityTransform.FromOverlay(transform),
            Settings.SessionTuning.CandidateStabilityPixels);
        var requiredFrames = Settings.SessionTuning.MediumConfidenceFrames;

        while (_candidateStability.Count < requiredFrames)
        {
            CapturedGameFrame? frame;
            bool ownsFrame;
            if (preparedConfirmationFrame is not null)
            {
                // Use the stability-retained confirmation frame — skip delay + capture.
                frame = preparedConfirmationFrame;
                ownsFrame = false;
                preparedConfirmationFrame = null; // consume once
            }
            else
            {
                var delayStart = Stopwatch.GetTimestamp();
                await Task.Delay(
                    Settings.SessionTuning.StableFrameIntervalMilliseconds,
                    cancellationToken);
                confirmationDelayMs += (Stopwatch.GetTimestamp() - delayStart)
                    * 1000d / Stopwatch.Frequency;
                var captureStart = Stopwatch.GetTimestamp();
                if (!_captureService.TryCaptureViewport(
                        Settings.MapViewportRegion!,
                        out frame,
                        out _)
                    || frame is null)
                {
                    confirmationCaptureMs += (Stopwatch.GetTimestamp() - captureStart)
                        * 1000d / Stopwatch.Frequency;
                    return null;
                }
                confirmationCaptureMs += (Stopwatch.GetTimestamp() - captureStart)
                    * 1000d / Stopwatch.Frequency;
                ownsFrame = true;
            }

            using (var frameDispose = ownsFrame ? frame : null)
            {
                // The previously locked session may belong to the primary
                // floor.  Confirmation carries only this target floor's own
                // transform/history, never a cross-floor player prior.
                MapViewportOrigin? predicted = null;
                var tuning = Settings.RecognitionTuning.Clone();
                var computeStart = Stopwatch.GetTimestamp();
                var attempt = await Task.Run(
                    () => _recognition.AlignFloorWithoutGates(
                        frame,
                        selectedMapId,
                        floorKey,
                        latest.Result.OverlayTransform!,
                        MapOverlayAlignmentMode.Uniform,
                        tuning,
                        structureTuning,
                        null,
                        predicted,
                        BuildLiveIgnoreRegions(frame),
                        _candidateStability.History,
                        isTracking: true,
                        useProjectedBoundaryMask: true),
                    cancellationToken);
                RecordResearchAttempt(
                    latest.Map,
                    floorKey,
                    frame,
                    attempt,
                    "stability-confirmation",
                    searchRadii: [structureTuning.TrackingScaleSearchRadius],
                    stableConfirmationFrames: attempt.Recognition is null
                        ? _candidateStability.Count
                        : _candidateStability.Count + 1,
                    stableConfirmationRequiredFrames: requiredFrames);
                confirmationComputeMs += (Stopwatch.GetTimestamp() - computeStart)
                    * 1000d / Stopwatch.Frequency;
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt.Recognition?.Result.OverlayTransform
                    is not { } candidateTransform
                    || attempt.Recognition.Result.Confidence
                        < Settings.SessionTuning.MediumConfidence)
                {
                    return null;
                }

                latest = attempt.Recognition;
                extraFrames++;
                _candidateStability.Observe(
                    MapSimilarityTransform.FromOverlay(candidateTransform),
                    Settings.SessionTuning.CandidateStabilityPixels);
                TransitionSession(
                    MapSessionState.Confirming,
                    mapId: selectedMapId,
                    floor: floorKey,
                    locationMethod: ToLocationMethod(latest.Result.Source),
                    confidence: latest.Result.Confidence,
                    stableCandidateFrames: _candidateStability.Count,
                    detail: $"{floorLabel} 中等置信度候选正在等待连续帧确认。");
            }
        }
        var totalConfirmMs = confirmTimer.Elapsed.TotalMilliseconds;
        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            $"{floorLabel} 候选稳定性确认完成 · {totalConfirmMs:F0}ms · 额外{extraFrames}帧",
            elapsedMs: totalConfirmMs,
            details: new()
            {
                ["extraFrames"] = extraFrames,
                ["delayMs"] = confirmationDelayMs,
                ["captureMs"] = confirmationCaptureMs,
                ["computeMs"] = confirmationComputeMs
            });
        return latest;
    }

    private async Task<RuntimeMapRecognition?> ConfirmAlignmentCandidateAsync(
        RuntimeMapRecognition initial,
        Guid selectedMapId,
        MapStructureRegistrationTuning structureTuning,
        CancellationToken cancellationToken,
        MapRecognitionAttempt? previousAttempt = null,
        CapturedGameFrame? preparedConfirmationFrame = null)
    {
        var confirmTimer = Stopwatch.StartNew();
        var extraFrames = 0;
        double confirmationDelayMs = 0d;
        double confirmationCaptureMs = 0d;
        double confirmationComputeMs = 0d;
        _candidateStability.Reset();
        var latest = initial;
        var transform = initial.Result.OverlayTransform;
        if (transform is null)
            return null;
        _candidateStability.Observe(
            MapSimilarityTransform.FromOverlay(transform),
            Settings.SessionTuning.CandidateStabilityPixels);
        var requiredFrames = Settings.SessionTuning.MediumConfidenceFrames;

        while (_candidateStability.Count < requiredFrames)
        {
            CapturedGameFrame? frame;
            bool ownsFrame;
            if (preparedConfirmationFrame is not null)
            {
                // Use the stability-retained confirmation frame — skip delay + capture.
                frame = preparedConfirmationFrame;
                ownsFrame = false;
                preparedConfirmationFrame = null; // consume once
            }
            else
            {
                var delayStart = Stopwatch.GetTimestamp();
                await Task.Delay(
                    Settings.SessionTuning.StableFrameIntervalMilliseconds,
                    cancellationToken);
                confirmationDelayMs += (Stopwatch.GetTimestamp() - delayStart)
                    * 1000d / Stopwatch.Frequency;
                var captureStart = Stopwatch.GetTimestamp();
                if (!_captureService.TryCaptureViewport(
                        Settings.MapViewportRegion!,
                        out frame,
                        out _)
                    || frame is null)
                {
                    confirmationCaptureMs += (Stopwatch.GetTimestamp() - captureStart)
                        * 1000d / Stopwatch.Frequency;
                    return null;
                }
                confirmationCaptureMs += (Stopwatch.GetTimestamp() - captureStart)
                    * 1000d / Stopwatch.Frequency;
                ownsFrame = true;
            }

            using (var frameDispose = ownsFrame ? frame : null)
            {
                var seed = MapAlignmentSession.FromRecognition(
                    latest.Map,
                    latest.Result);
                var predicted = PredictViewportOrigin(
                    latest.Map,
                    frame,
                    seed);
                var tuning = Settings.RecognitionTuning.Clone();
                var computeStart = Stopwatch.GetTimestamp();
                var attempt = await Task.Run(
                    () => previousAttempt is not null
                        ? _recognition.ConfirmSelectedAlignment(
                            frame,
                            selectedMapId,
                            seed,
                            previousAttempt,
                            MapOverlayAlignmentMode.Uniform,
                            tuning,
                            structureTuning,
                            _lastTrustedPlayerPoint,
                            predicted,
                            BuildLiveIgnoreRegions(frame),
                            _candidateStability.History,
                            nativeScaleChangeRatio:
                                Settings.SessionTuning.NativeScaleChangeRatio,
                            mapClass: _matchSession.Snapshot.MapClass)
                        : _recognition.AlignSelected(
                            frame,
                            selectedMapId,
                            seed,
                            MapOverlayAlignmentMode.Uniform,
                            tuning,
                            structureTuning,
                            _lastTrustedPlayerPoint,
                            predicted,
                            BuildLiveIgnoreRegions(frame),
                            _candidateStability.History,
                            alignmentSearchContext: null,
                            nativeScaleChangeRatio:
                                Settings.SessionTuning.NativeScaleChangeRatio,
                            mapClass: _matchSession.Snapshot.MapClass),
                    cancellationToken);
                RecordResearchAttempt(
                    latest.Map,
                    latest.Result.Floor,
                    frame,
                    attempt,
                    "stability-confirmation",
                    stableConfirmationFrames: attempt.Recognition is null
                        ? _candidateStability.Count
                        : _candidateStability.Count + 1,
                    stableConfirmationRequiredFrames: requiredFrames);
                confirmationComputeMs += (Stopwatch.GetTimestamp() - computeStart)
                    * 1000d / Stopwatch.Frequency;
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt.Recognition?.Result.OverlayTransform
                    is not { } candidateTransform
                    || attempt.Recognition.Result.Confidence
                        < Settings.SessionTuning.MediumConfidence)
                {
                    return null;
                }

                latest = attempt.Recognition;
                extraFrames++;
                _candidateStability.Observe(
                    MapSimilarityTransform.FromOverlay(candidateTransform),
                    Settings.SessionTuning.CandidateStabilityPixels);
                TransitionSession(
                    MapSessionState.Confirming,
                    mapId: selectedMapId,
                    floor: latest.Result.Floor,
                    locationMethod: ToLocationMethod(latest.Result.Source),
                    confidence: latest.Result.Confidence,
                    stableCandidateFrames: _candidateStability.Count,
                    detail: "Waiting for a stable medium-confidence candidate.");
            }
        }
        var totalConfirmMs = confirmTimer.Elapsed.TotalMilliseconds;
        LogCollector.Append(MapLogCategory.ScanLifecycle, MapLogLevel.Info,
            $"候选稳定性确认完成 · {totalConfirmMs:F0}ms · 延迟{confirmationDelayMs:F0}ms · 截帧{confirmationCaptureMs:F0}ms · 计算{confirmationComputeMs:F0}ms · 额外{extraFrames}帧",
            elapsedMs: totalConfirmMs,
            details: new()
            {
                ["extraFrames"] = extraFrames,
                ["delayMs"] = confirmationDelayMs,
                ["captureMs"] = confirmationCaptureMs,
                ["computeMs"] = confirmationComputeMs
            });
        return latest;
    }

    private static double GetAttemptConfidence(MapRecognitionAttempt attempt)
    {
        var confidence = attempt.Recognition?.Result.Confidence
            ?? attempt.StructureResult?.Confidence
            ?? 0d;
        return double.IsFinite(confidence)
            ? Math.Clamp(confidence, 0d, 1d)
            : 0d;
    }

    private static string FormatAlignmentFailure(
        MapRecognitionAttempt attempt,
        MapStructureRegistrationTuning tuning)
    {
        var reason = string.IsNullOrWhiteSpace(attempt.FailureReason)
            ? "对齐未通过"
            : attempt.FailureReason.Trim();
        var confidence = GetAttemptConfidence(attempt);
        if (attempt.StructureResult is not { } structure)
            return $"{reason}（置信度 {confidence:P1}）";

        var details = $"置信度 {confidence:P1}；结构分 {structure.BestScore:F3}"
            + $"；候选差距 {structure.CandidateMargin:P1}";
        if (structure.Candidates.FirstOrDefault() is { } best)
        {
            details += $"；边缘距离 {best.ChamferPixels:F2}px"
                + $" / 上限 {tuning.MaximumChamferPixels:F2}px"
                + $"；边缘覆盖率 {best.EdgeCoverage:P1}"
                + $" / 下限 {tuning.MinimumEdgeCoverage:P1}";
        }
        return $"{reason}（{details}）";
    }

    private void RecordResearchAttempt(
        MapRecord map,
        string floorKey,
        CapturedGameFrame frame,
        MapRecognitionAttempt attempt,
        string floorSource,
        FloorScaleSeed? scaleSeed = null,
        IReadOnlyList<double>? searchRadii = null,
        int stableConfirmationFrames = 0,
        int stableConfirmationRequiredFrames = 0,
        bool calibrationUpdated = false,
        string? calibrationRejectionReason = null)
    {
        if (!_researchCollector.IsEnabled)
            return;
        var structure = attempt.StructureResult;
        var transform = attempt.Recognition?.Result.OverlayTransform
            ?? structure?.Transform;
        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(map);
        var learned = Settings.FloorScaleCalibrations.FirstOrDefault(candidate =>
            candidate.Matches(map.Id, map.UpdatedAt, primaryFloorKey, floorKey));
        var confidence = attempt.Recognition?.Result.Confidence
            ?? structure?.Confidence
            ?? 0d;
        _researchCollector.Record(
            new MapAlignmentResearchAttempt
            {
                MapId = map.Id,
                MapUpdatedAt = map.UpdatedAt,
                FloorKey = floorKey,
                FloorPosition = MapFloorRules.GetFloorPosition(map, floorKey),
                FloorSource = floorSource,
                WindowSignature = CreateWindowSignature(frame),
                ReferenceWidth = transform?.ReferenceWidth
                    ?? structure?.ReferenceWidth
                    ?? 0,
                ReferenceHeight = transform?.ReferenceHeight
                    ?? structure?.ReferenceHeight
                    ?? 0,
                ValidMapBounds = MapFloorRules.GetFloorProfile(map, floorKey)
                    ?.GetEffectiveValidMapBounds(),
                PrimaryScale = scaleSeed?.PrimaryScale
                    ?? (string.Equals(floorKey, primaryFloorKey, StringComparison.Ordinal)
                        ? transform?.ScaleX
                        : null),
                HistoricalFloorRatio = learned?.MedianRatio,
                ScaleSeedSource = scaleSeed?.Source ?? "double-gate",
                SearchStages = (searchRadii ?? [])
                    .Select(radius => new MapAlignmentResearchSearchStage(
                        radius,
                        structure?.ScaleHypothesisCount ?? 0,
                        UsedGlobalTranslationSearch: true))
                    .ToArray(),
                QueryEdgePixels = structure?.QueryEdgePixels ?? 0,
                QueryBoundsWidth = structure?.QueryBoundsWidth ?? 0,
                QueryBoundsHeight = structure?.QueryBoundsHeight ?? 0,
                FeatureMatchCount = structure?.FeatureMatchCount ?? 0,
                FeatureInlierCount = structure?.FeatureInlierCount ?? 0,
                GateCandidateCount = attempt.Diagnostics.GateCandidateCount,
                AnchorMatches = attempt.Recognition?.Result.AnchorMatches ?? [],
                EvidenceKind = attempt.Recognition?.Result.EvidenceKind
                    ?? MapAlignmentEvidenceKind.None,
                Candidates = structure?.Candidates.Take(20).ToArray() ?? [],
                ConfidenceBreakdown = structure?.ConfidenceBreakdown,
                FinalTransform = transform,
                Confidence = confidence,
                IsHighConfidence = confidence
                    >= Settings.SessionTuning.HighConfidence,
                Accepted = attempt.Recognition is not null,
                StableConfirmationFrames = stableConfirmationFrames,
                StableConfirmationRequiredFrames =
                    stableConfirmationRequiredFrames,
                CalibrationUpdated = calibrationUpdated,
                CalibrationRejectionReason = calibrationRejectionReason,
                ElapsedMilliseconds = (structure?.PreprocessMilliseconds ?? 0d)
                    + (structure?.SearchMilliseconds ?? 0d)
                    + (structure?.RefineMilliseconds ?? 0d),
                FailureCategory = MapAlignmentResearchFailureClassifier.Classify(attempt),
                FailureReason = attempt.FailureReason
            },
            frame.Image);
    }

    private async Task SaveAlignmentCalibrationAsync(
        RuntimeMapRecognition recognition,
        MapWindowSignature signature)
    {
        var transform = recognition.Result.OverlayTransform!;
        var proposed = Settings.Clone();
        proposed.AlignmentCalibrations.RemoveAll(candidate =>
            candidate.MapId == recognition.Map.Id
            && candidate.Floor == recognition.Result.Floor
            && candidate.ClientWidth == signature.ClientWidth
            && candidate.ClientHeight == signature.ClientHeight
            && candidate.ViewportWidth == signature.ViewportWidth
            && candidate.ViewportHeight == signature.ViewportHeight
            && candidate.Dpi == signature.Dpi);
        proposed.AlignmentCalibrations.Add(new MapAlignmentCalibration
        {
            MapId = recognition.Map.Id,
            Floor = recognition.Result.Floor,
            MapUpdatedAt = recognition.Map.UpdatedAt,
            ReferenceWidth = transform.ReferenceWidth,
            ReferenceHeight = transform.ReferenceHeight,
            UniformScale = (transform.ScaleX + transform.ScaleY) / 2d,
            RotationDegrees = transform.OrientationDegrees,
            ClientWidth = signature.ClientWidth,
            ClientHeight = signature.ClientHeight,
            ViewportWidth = signature.ViewportWidth,
            ViewportHeight = signature.ViewportHeight,
            Dpi = signature.Dpi,
            Confidence = recognition.Result.Confidence,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        proposed.AlignmentCalibrations = proposed.AlignmentCalibrations
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .Take(128)
            .ToList();
        await _settingsRepository.SaveAsync(proposed);
        Settings = proposed;
    }

    private async Task<string?> SaveFloorScaleCalibrationAsync(
        RuntimeMapRecognition recognition,
        double primaryScale)
    {
        var targetTransform = recognition.Result.OverlayTransform;
        if (targetTransform is null
            || !double.IsFinite(primaryScale)
            || primaryScale <= 0d)
        {
            return "invalid-primary-or-target-scale";
        }

        var primaryFloorKey = MapFloorRules.GetPrimaryFloorKey(recognition.Map);
        var floorKey = recognition.Result.Floor;
        if (string.Equals(primaryFloorKey, floorKey, StringComparison.Ordinal))
            return null;

        var ratio = ((targetTransform.ScaleX + targetTransform.ScaleY) / 2d)
            / primaryScale;
        var proposed = Settings.Clone();
        var calibration = proposed.FloorScaleCalibrations
            .FirstOrDefault(candidate => candidate.Matches(
                recognition.Map.Id,
                recognition.Map.UpdatedAt,
                primaryFloorKey,
                floorKey));
        if (calibration is null)
        {
            calibration = new MapFloorScaleCalibration
            {
                MapId = recognition.Map.Id,
                MapUpdatedAt = recognition.Map.UpdatedAt,
                PrimaryFloorKey = primaryFloorKey,
                FloorKey = floorKey
            };
            proposed.FloorScaleCalibrations.Add(calibration);
        }

        if (!calibration.TryAddTrustedSample(
                ratio,
                recognition.Result.Confidence,
                DateTimeOffset.UtcNow,
                out var rejectionReason))
        {
            return rejectionReason;
        }

        proposed.FloorScaleCalibrations = proposed.FloorScaleCalibrations
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .Take(256)
            .ToList();
        try
        {
            await _settingsRepository.SaveAsync(proposed);
            Settings = proposed;
            return null;
        }
        catch (Exception exception)
        {
            LogCollector.Append(
                MapLogCategory.Session,
                MapLogLevel.Warning,
                $"Floor-scale calibration persistence failed after lock: {exception.Message}",
                details: new()
                {
                    ["mapId"] = recognition.Map.Id,
                    ["floor"] = floorKey,
                    ["exceptionType"] = exception.GetType().FullName
                });
            return $"persistence-failed:{exception.GetType().Name}";
        }
    }

    private void StartSessionMonitor()
    {
        if (_disposed
            || !Settings.IsEnabled
            || !_matchSession.Snapshot.IsStarted
            || _sessionMonitorCancellation is not null)
        {
            return;
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token,
            _matchCancellation?.Token ?? CancellationToken.None);
        _sessionMonitorCancellation = cancellation;
        _sessionMonitorTask = RunSessionMonitorAsync(cancellation.Token);
    }

    private async Task StopSessionMonitorAsync()
    {
        var cancellation = Interlocked.Exchange(
            ref _sessionMonitorCancellation,
            null);
        var task = Interlocked.Exchange(ref _sessionMonitorTask, null);
        if (cancellation is null && task is null)
            return;

        cancellation?.Cancel();
        try
        {
            if (task is not null)
                await task;
        }
        catch (OperationCanceledException)
            when (cancellation?.IsCancellationRequested is true)
        {
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task RunSessionMonitorAsync(CancellationToken cancellationToken)
    {
        var nextPresence = DateTimeOffset.MinValue;
        var nextPlayer = DateTimeOffset.MinValue;
        var nextWindow = DateTimeOffset.MinValue;
        var floorChangeTracker = new MapFloorChangeTracker();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Visual presence is validation evidence for a session that
                // was explicitly opened by input. It must never create a map
                // session on its own: a false-positive floor match in normal
                // gameplay would otherwise start an alignment, fail, close,
                // and immediately start the same cycle again.
                if (!MapSessionRules.ShouldRunPassiveSessionMonitor(
                        SessionSnapshot.State,
                        IsScanning))
                {
                    floorChangeTracker.Reset();
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                if (!_captureService.TryGetForegroundClientBounds(
                        out var clientBounds,
                        out var windowHandle,
                        out _))
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (now >= nextPresence)
                {
                    nextPresence = now.AddMilliseconds(
                        Settings.SessionTuning.PresencePollingMilliseconds);
                    string? floor = null;
                    var mapPresent =
                        Settings.FloorDisplayRegion?.IsValid is true
                        && _floorRecognition.TryRecognizePresence(
                            Settings.FloorDisplayRegion,
                            out floor,
                            out _,
                            Settings.FloorRecognitionTuning);
                    if (mapPresent)
                    {
                        if (floor is not null)
                        {
                            var detectedFloorKey = SelectedMap is null
                                ? floor
                                : MapFloorRules.GetFloorKeyAtPosition(
                                    SelectedMap,
                                    string.Equals(floor, "2f", StringComparison.Ordinal)
                                        ? 2
                                        : 1) ?? floor;
                            var differsFromLock = !string.Equals(
                                detectedFloorKey,
                                SessionSnapshot.Floor,
                                StringComparison.Ordinal);
                            var requiresStableFloorChange = differsFromLock
                                && SessionSnapshot.IsLocked
                                && SessionSnapshot.LocationMethod
                                    != MapLocationMethod.Manual
                                && !string.Equals(
                                    LastRecognition?.Result.Floor,
                                    detectedFloorKey,
                                    StringComparison.Ordinal);
                            var floorChangeConfirmed =
                                !requiresStableFloorChange
                                || floorChangeTracker.Observe(
                                    SessionSnapshot.Floor,
                                    detectedFloorKey);
                            if (floorChangeConfirmed
                                && !Settings.PersistentMiniMapEnabled
                                && _manualFloorOverrideKey is null)
                            {
                                CurrentFloorKey = detectedFloorKey;
                            }
                            if (requiresStableFloorChange
                                && floorChangeConfirmed)
                            {
                                // Preserve the current background until an
                                // explicit reopen requests the newly detected floor.
                                LoseLockedSession(
                                    MapRecalibrationReason.FloorChanged,
                                    $"The passive floor probe reported {detectedFloorKey}. The current background was retained until the game map is explicitly reopened.");
                                continue;
                            }
                            if (string.Equals(
                                    detectedFloorKey,
                                    SessionSnapshot.Floor,
                                    StringComparison.Ordinal))
                            {
                                floorChangeTracker.Reset();
                            }
                            if (SelectedMap is not null
                                && string.Equals(
                                    detectedFloorKey,
                                    MapFloorRules.GetPrimaryFloorKey(SelectedMap),
                                    StringComparison.Ordinal)
                                && SessionSnapshot.State
                                    == MapSessionState.Lost)
                            {
                                TransitionSession(
                                    MapSessionState.RecalibrationRequired,
                                    floor: detectedFloorKey,
                                    reason:
                                        MapRecalibrationReason.AlignmentLost,
                                    detail:
                                        "First floor returned; reopen the map to request one new alignment.");
                                continue;
                            }
                        }
                        else
                        {
                            floorChangeTracker.Reset();
                        }
                    }
                    else
                    {
                        floorChangeTracker.Reset();
                        // Input is the authoritative map-close signal. A
                        // failed passive floor probe cannot prove that the
                        // game map closed, so never clear a locked background
                        // or reset the side-button state from this path.
                    }
                }

                if (!SessionSnapshot.IsLocked)
                {
                    await Task.Delay(50, cancellationToken);
                    continue;
                }

                if (now >= nextWindow)
                {
                    nextWindow = now.AddMilliseconds(
                        Settings.SessionTuning.WindowValidationMilliseconds);
                    var viewportBounds = DwrGameWindowCaptureService
                        .GetViewportBounds(
                            clientBounds,
                            Settings.MapViewportRegion!);
                    var currentSignature = CreateWindowSignature(
                        clientBounds,
                        viewportBounds,
                        windowHandle);
                    if (_lockedWindowSignature is { } locked
                        && !locked.Equals(currentSignature))
                    {
                        LoseLockedSession(
                            MapSessionRules.GetSignatureChangeReason(
                                locked,
                                currentSignature));
                        continue;
                    }
                }

                if (now >= nextPlayer)
                {
                    nextPlayer = now.AddMilliseconds(
                        Settings.SessionTuning.PlayerPollingMilliseconds);
                    TrackPlayer();
                }

                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // A bad capture or native-window query must not permanently
                // terminate the monitor.  It also cannot prove that the game
                // map was closed, so preserve the displayed background and
                // retry after a short backoff.
                LogCollector.Append(
                    MapLogCategory.Session,
                    MapLogLevel.Error,
                    $"被动会话监控异常，将继续重试：{exception.Message}");
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private void TrackPlayer()
    {
        if (!Settings.PlayerTrackingEnabled)
        {
            HideStalePlayerIfNeeded();
            return;
        }
        var snapshot = SessionSnapshot;
        var match = _matchSession.Snapshot;
        if (!match.IsStarted
            || match.PlayerSlot is not { } playerSlot
            || !snapshot.IsLocked
            || !ArePlayerAssetsReady
            || Settings.MapViewportRegion is null
            || SelectedMap is null
            || !_captureService.TryCaptureViewport(
                Settings.MapViewportRegion,
                out var frame,
                out _)
            || frame is null)
        {
            HideStalePlayerIfNeeded();
            return;
        }

        using (frame)
        {
            var detection = _playerMarkerDetector.Detect(
                frame.Image,
                frame.ViewportBounds,
                frame.ClientBounds,
                playerSlot,
                MapPlayerAssetCatalog.ResolvePath(playerSlot),
                snapshot.Player?.ViewportPoint,
                Settings.PlayerTrackingTuning);
            if (!detection.Succeeded
                || detection.Confidence
                    < Settings.PlayerTrackingTuning.MinimumConfidence)
            {
                HideStalePlayerIfNeeded();
                return;
            }

            var reference = snapshot.LockedTransform!.ToReference(
                detection.ScreenPoint);
            var bounds = (MapFloorRules.GetFloorProfile(
                    SelectedMap,
                    snapshot.Floor)
                ?? SelectedMap.Recognition.FirstFloor)
                .GetEffectiveValidMapBounds();
            if (!bounds.Contains(reference, tolerance: 1d))
            {
                HideStalePlayerIfNeeded();
                return;
            }

            var player = new MapPlayerState
            {
                PlayerSlot = detection.PlayerSlot,
                ViewportPoint = detection.ViewportPoint,
                ScreenPoint = detection.ScreenPoint,
                ReferencePoint = bounds.Clamp(reference),
                MarkerWidth = detection.LocalBounds.Width,
                MarkerHeight = detection.LocalBounds.Height,
                Confidence = detection.Confidence,
                ObservedAt = DateTimeOffset.UtcNow
            };
            _lastPlayerObservedAt = player.ObservedAt;
            _lastTrustedPlayerPoint = player.ReferencePoint;
            lock (_sessionStateGate)
                _mapOpenSession.UpdatePlayer(player);
            _overlay.UpdatePlayer(player);
            _dispatcher.TryEnqueue(NotifyStateChanged);
        }
    }

    private void HideStalePlayerIfNeeded()
    {
        if (SessionSnapshot.Player is null
            || DateTimeOffset.UtcNow - _lastPlayerObservedAt
                <= TimeSpan.FromMilliseconds(
                    Settings.PlayerTrackingTuning.StaleHideMilliseconds))
        {
            return;
        }
        lock (_sessionStateGate)
            _mapOpenSession.UpdatePlayer(null);
        _overlay.UpdatePlayer(null);
        _dispatcher.TryEnqueue(NotifyStateChanged);
    }

    private void LoseLockedSession(
        MapRecalibrationReason reason,
        string? detail = null)
    {
        if (!SessionSnapshot.IsLocked)
            return;
        _alignmentCommitGuard.Invalidate();
        TransitionSession(
            MapSessionState.Lost,
            reason: reason,
            detail:
                detail
                ?? $"Locked background requires recalibration: {reason}. Reopen the map to request one new alignment.");

        // A passive probe can become temporarily unreliable while the game is
        // animating or changing focus.  It may stop player tracking and ask
        // for a fresh alignment, but it is not proof that the game map was
        // closed.  Clearing here made a valid background flash for one frame
        // and resetting the toggle state then required multiple side-button
        // presses before the next real close was recognised.
        _alignmentSession = null;
        _lockedWindowSignature = null;
        _lastTrustedPlayerPoint = null;
        _lastPlayerObservedAt = DateTimeOffset.MinValue;
        _playerMarkerDetector.ResetTracking();
        lock (_sessionStateGate)
            _mapOpenSession.UpdatePlayer(null);
        _overlay.UpdatePlayer(null);

        StatusMessage =
            $"检测到对齐环境变化（{reason}）；已保留当前地图。"
            + "请按一次游戏地图开关键关闭后再打开，以重新对齐。";
        _currentOverlayStatus = new MapOverlayStatus(
            MapOverlayStatusLevel.Warning,
            "当前地图等待重新对齐",
            StatusMessage,
            "被动监控不会再自动清空已显示的地图。");
        TryPublishOverlayStatus(
            _currentOverlayStatus,
            showImmediately: _overlay.IsVisible);
        LogCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Warning,
            "被动监控要求重新对齐；已保留当前地图和游戏地图开关状态。",
            details: new()
            {
                ["reason"] = reason.ToString(),
                ["detail"] = detail ?? string.Empty,
                ["gameMapIsOpen"] = _gameMapToggleState.IsOpen
            });
        _dispatcher.TryEnqueue(NotifyStateChanged);
    }

    private void CloseMapSession(string detail)
    {
        var previousSnapshot = SessionSnapshot;
        var gameMapWasOpen = _gameMapToggleState.IsOpen;
        CancelPendingGameMapRefresh();
        _alignmentCommitGuard.Invalidate();
        _overlay.ClearSession();
        if (!Settings.PersistentMiniMapEnabled)
            _overlay.Hide();
        _alignmentSession = null;
        _lockedWindowSignature = null;
        if (!Settings.PersistentMiniMapEnabled)
        {
            _manualFloorOverrideKey = null;
            LastRecognition = null;
        }
        _alignmentTrackingMode = SelectedMap is null
            ? MapAlignmentTrackingMode.None
            : MapAlignmentTrackingMode.NeedsGatePair;
        _candidateStability.Reset();
        _playerMarkerDetector.ResetTracking();
        _gameMapToggleState.Reset();
        MapSessionSnapshot closedSnapshot;
        lock (_sessionStateGate)
            closedSnapshot = _mapOpenSession.Close(detail);
        LogCollector.Append(
            MapLogCategory.Session,
            MapLogLevel.Info,
            "Map-open alignment session closed.",
            details: new()
            {
                ["detail"] = detail,
                ["mapId"] = previousSnapshot.MapId,
                ["floor"] = previousSnapshot.Floor,
                ["previousState"] = previousSnapshot.State.ToString(),
                ["previousSessionVersion"] = previousSnapshot.Version,
                ["sessionVersion"] = closedSnapshot.Version,
                ["previousAlignmentRevision"] =
                    previousSnapshot.AlignmentRevision,
                ["alignmentRevision"] = closedSnapshot.AlignmentRevision,
                ["gameMapWasOpen"] = gameMapWasOpen
            });
        ResetRecognitionStatisticsAttempt();
        _dispatcher.TryEnqueue(NotifyStateChanged);
    }

    private void ClearMatchScopedMapState()
    {
        Settings = MapMatchLifecycleRules.CreateSettingsWithoutMatchSelection(
            Settings);
        SelectedMap = null;
        _selectedMapLease.Clear();
        _recognition.ResetMatchState();
        _alignmentSession = null;
        _alignmentTrackingMode = MapAlignmentTrackingMode.None;
        _lockedWindowSignature = null;
        _manualFloorOverrideKey = null;
        _lastTrustedPlayerPoint = null;
        _lastPlayerObservedAt = DateTimeOffset.MinValue;
        CurrentFloorKey = null;
        LastFloorRecognition = null;
        LastRecognition = null;
        LastDiagnostics = null;
        _candidateStability.Reset();
        _playerMarkerDetector.ResetTracking();
        _overlay.ClearPersistentMiniMap();
    }

    /// <summary>
    /// Cleans up an alignment attempt that ended without locking a background.
    /// The game's map is still open, so preserve the open toggle state; only
    /// release the claimed pipeline and transient alignment state.
    /// </summary>
    private void AbandonOpenAlignment(string detail)
    {
        if (Settings.PersistentMiniMapEnabled)
        {
            TryRestorePersistentMiniMap();
            try { _overlay.Show(); }
            catch { /* Keep alignment recovery independent of overlay recovery. */ }
        }
        else
        {
            _overlay.Hide();
        }

        _alignmentSession = null;
        _lockedWindowSignature = null;
        _alignmentTrackingMode = SelectedMap is null
            ? MapAlignmentTrackingMode.None
            : MapAlignmentTrackingMode.NeedsGatePair;
        _candidateStability.Reset();
        _playerMarkerDetector.ResetTracking();
        _gameMapToggleState.ReleaseOpenPipeline();
        LogCollector.Append(
            MapLogCategory.ScanLifecycle,
            MapLogLevel.Warning,
            $"Alignment did not lock a background; released the open-map pipeline: {detail}",
            details: new()
            {
                ["sessionState"] = SessionSnapshot.State.ToString(),
                ["gameMapIsOpen"] = _gameMapToggleState.IsOpen
            });
        _dispatcher.TryEnqueue(NotifyStateChanged);
    }

    private void ApplyInputBindings(MapRuntimeSettings settings) =>
        _input.ApplyBindings(
            settings.QuickScanBinding,
            settings.OverlayToggleBinding,
            settings.ManualRecognitionBinding,
            settings.GameMapToggleBinding,
            settings.ControlPanelToggleBinding,
            settings.SwitchFloorBinding);

    private static void EnsureBindingsAreDistinct(MapRuntimeSettings settings)
    {
        var configured = new[]
        {
            settings.QuickScanBinding,
            settings.OverlayToggleBinding,
            settings.ManualRecognitionBinding,
            settings.GameMapToggleBinding,
            settings.ControlPanelToggleBinding,
            settings.SwitchFloorBinding
        }.Where(binding => binding.IsConfigured).ToArray();
        for (var left = 0; left < configured.Length - 1; left++)
        {
            for (var right = left + 1; right < configured.Length; right++)
            {
                if (configured[left].Equals(configured[right]))
                    throw new InvalidOperationException("游戏地图开关和其他全局操作不能使用同一个按键。");
            }
        }
    }

    private static bool IsModifierKey(uint key) => key is 16 or 17 or 18 or 91 or 92;

    private static string ResolveFloorReferencePath(string fileName)
    {
        var deployed = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            fileName);
        if (File.Exists(deployed))
            return deployed;
        var project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "Assets",
            fileName));
        if (File.Exists(project))
            return project;
        var current = Path.Combine(
            Environment.CurrentDirectory,
            "Assets",
            fileName);
        return File.Exists(current) ? current : deployed;
    }

    private void ResetGameMapToggleState()
    {
        _gameMapToggleState.Reset();
        CancelPendingGameMapRefresh();
        _overlay.Hide();
    }

    private void TryRestorePersistentMiniMap() => RefreshMiniMapForCurrentFloor();

    private void RefreshMiniMapForCurrentFloor()
    {
        if (!Settings.PersistentMiniMapEnabled || SelectedMap is null)
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }

        MapOverlayTransform? transform = null;
        string effectiveFloorKey;

        if (LastRecognition is { } recognition
            && recognition.Result.OverlayTransform is { } existingTransform)
        {
            transform = existingTransform;
            effectiveFloorKey = _manualFloorOverrideKey ?? recognition.Result.Floor;
        }
        else
        {
            effectiveFloorKey = _manualFloorOverrideKey ?? "1f";
            var floorProfile = MapFloorRules.GetFloorProfile(SelectedMap, effectiveFloorKey)
                ?? SelectedMap.Recognition.FirstFloor;
            transform = new MapOverlayTransform
            {
                ReferenceWidth = floorProfile.RecognitionPixelWidth,
                ReferenceHeight = floorProfile.RecognitionPixelHeight,
                ScaleX = 1.0,
                ScaleY = 1.0,
                OffsetX = 0,
                OffsetY = 0,
                ReferenceCenterX = floorProfile.RecognitionPixelWidth / 2.0,
                ReferenceCenterY = floorProfile.RecognitionPixelHeight / 2.0,
                ScreenCenterX = 0,
                ScreenCenterY = 0,
                OrientationDegrees = 0,
                AlignmentMode = Settings.OverlayAlignmentMode
            };
        }

        if (transform is null)
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }

        var overlayPath = _mapRepository.GetFloorOverlayPath(SelectedMap, effectiveFloorKey);
        if (!File.Exists(overlayPath))
        {
            _overlay.ClearPersistentMiniMap();
            return;
        }
        var profile = MapFloorRules.GetFloorProfile(
                SelectedMap,
                effectiveFloorKey)
            ?? SelectedMap.Recognition.FirstFloor;
        var anchors = profile.Anchors
            .Where(anchor => anchor.Bounds?.IsValid is true)
            .Select(anchor => new MapOverlayRenderAnchor(
                anchor.Key,
                anchor.DisplayName,
                anchor.Bounds!.Clone()))
            .ToArray();
        var annotations = profile.Annotations
            .Where(a => a.IsValid)
            .Select(a => new MapOverlayRenderAnnotation(
                a.Type,
                a.ColorIndex,
                a.Bounds.Clone(),
                a.Text))
            .ToArray();
        var floorLabel = MapFloorRules.GetFloorDisplayName(
            SelectedMap,
            effectiveFloorKey);
        _overlay.SetPersistentMiniMapState(
            overlayPath,
            transform,
            _lastGameBounds,
            _lastGameWindowHandle,
            Settings.MiniMapScale,
            anchors,
            annotations,
            floorLabel);
    }

    private void HandleSwitchFloor()
    {
        if (LastRecognition is not { } recognition)
            return;
        // 楼层切换不依赖 session 状态（session 关闭后小地图仍可显示），
        // 只检查是否有可用的地图识别结果与变换数据。
        if (recognition.Result.OverlayTransform is null)
            return;
        // Use the map's ordered floor definitions rather than the legacy
        // 1F/2F enum. Imported maps may contain any number of floors.
        var nextFloorKey = MapFloorRules.GetNextFloorKey(
            recognition.Map,
            _manualFloorOverrideKey ?? recognition.Result.Floor);
        if (nextFloorKey is null)
            return;
        _manualFloorOverrideKey = nextFloorKey;
        CurrentFloorKey = _manualFloorOverrideKey;
        RefreshMiniMapForCurrentFloor();
        var floorLabel = MapFloorRules.GetFloorDisplayName(
            recognition.Map,
            _manualFloorOverrideKey);
        StatusMessage = $"已手动切换到{floorLabel}（仅小地图）";
        try { _overlay.Show(); }
        catch { /* 热路径中忽略渲染失败 */ }
        NotifyStateChanged();
    }

    private void CancelPendingGameMapRefresh()
    {
        var pending = Interlocked.Exchange(
            ref _gameMapRefreshCancellation,
            null);
        pending?.Cancel();
    }

    private void CancelMatchWork()
    {
        var cancellation = Interlocked.Exchange(
            ref _matchCancellation,
            null);
        if (cancellation is null)
            return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private Task StartTrackedOperation(
        Func<CancellationToken, Task> operation)
    {
        Task task;
        lock (_activeOperationsGate)
        {
            if (_disposed)
                return Task.CompletedTask;

            try
            {
                task = operation(_lifetimeCancellation.Token);
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
            _activeOperations.Add(task);
        }

        _ = ObserveTrackedOperationAsync(task);
        return task;
    }

    private IDisposable EnterApiOperation()
    {
        lock (_apiOperationsGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _apiOperationCount++;
        }
        return new ApiOperationLease(this);
    }

    private void ExitApiOperation()
    {
        TaskCompletionSource<bool>? drained = null;
        lock (_apiOperationsGate)
        {
            _apiOperationCount--;
            if (_apiOperationCount == 0)
                drained = _apiOperationsDrained;
        }
        drained?.TrySetResult(true);
    }

    private async Task ObserveTrackedOperationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Tracked map operation failed: {exception}");
        }
        finally
        {
            lock (_activeOperationsGate)
                _activeOperations.Remove(task);
        }
    }

    private void BeginRecognitionStatisticsAttempt()
    {
        lock (_recognitionStatisticsGate)
        {
            _recognitionAttemptStarted = true;
            _recognitionAttemptProducedAlignment = false;
            QueueRecognitionStatisticsWriteLocked(
                _recognitionStatisticsRepository.RecordAttemptStartedAsync);
        }
    }

    private void MarkRecognitionStatisticsAlignmentProduced()
    {
        lock (_recognitionStatisticsGate)
        {
            if (!_recognitionAttemptStarted
                || _recognitionAttemptProducedAlignment)
            {
                return;
            }

            _recognitionAttemptProducedAlignment = true;
            QueueRecognitionStatisticsWriteLocked(
                _recognitionStatisticsRepository.RecordAlignmentProducedAsync);
        }
    }

    private void ResetRecognitionStatisticsAttempt()
    {
        lock (_recognitionStatisticsGate)
        {
            _recognitionAttemptStarted = false;
            _recognitionAttemptProducedAlignment = false;
        }
    }

    private void QueueRecognitionStatisticsWriteLocked(
        Func<CancellationToken, Task> write)
    {
        _recognitionStatisticsWriteTask = RunRecognitionStatisticsWriteAsync(
            _recognitionStatisticsWriteTask,
            write);
    }

    private static async Task RunRecognitionStatisticsWriteAsync(
        Task previous,
        Func<CancellationToken, Task> write)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch
        {
            // A previous counter write must not prevent later attempts.
        }

        try
        {
            await write(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"Recognition statistics persistence failed: {exception}");
        }
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        var task = GetOrCreateDisposeTask();
        if (!_dispatcher.HasThreadAccess)
            task.GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync() =>
        new(GetOrCreateDisposeTask());

    private Task GetOrCreateDisposeTask()
    {
        lock (_disposeGate)
            return _disposeTask ??= DisposeAsyncCore();
    }

    private async Task DisposeAsyncCore()
    {
        Task[] activeOperations;
        lock (_activeOperationsGate)
        {
            _disposed = true;
            activeOperations = [.. _activeOperations];
        }
        Task apiOperationsDrained;
        lock (_apiOperationsGate)
        {
            if (_apiOperationCount == 0)
            {
                apiOperationsDrained = Task.CompletedTask;
            }
            else
            {
                _apiOperationsDrained ??=
                    new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                apiOperationsDrained = _apiOperationsDrained.Task;
            }
        }

        _integrityMonitor?.Dispose();
        _integrityMonitor = null;
        _input.Dispose();
        _lifetimeCancellation.Cancel();
        await _researchCollector.DisposeAsync();
        await LogCollector.DisposeAsync();
        _gameMapToggleState.Reset();
        CancelMatchWork();
        CancelPendingGameMapRefresh();
        await StopSessionMonitorAsync();

        if (activeOperations.Length > 0)
        {
            try
            {
                await Task.WhenAll(activeOperations);
            }
            catch (OperationCanceledException)
                when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Map operation failed during shutdown: {exception}");
            }
        }
        await apiOperationsDrained;

        Task recognitionStatisticsWrites;
        lock (_recognitionStatisticsGate)
            recognitionStatisticsWrites = _recognitionStatisticsWriteTask;
        await recognitionStatisticsWrites;

        CancelMatchWork();
        CancelPendingGameMapRefresh();
        await StopSessionMonitorAsync();

        await _initializeGate.WaitAsync();
        _initializeGate.Release();
        await _scanGate.WaitAsync();
        _scanGate.Release();

        _floorRecognition.Dispose();
        _playerMarkerDetector.Dispose();
        _recognition.Dispose();
        _overlay.Dispose();
        _controlPanel.Dispose();
        _initializeGate.Dispose();
        _scanGate.Dispose();
        _lifetimeCancellation.Dispose();
        StateChanged = null;
        ElevationRequiredDetected = null;
    }

    private sealed class MapAlignmentConfirmationException(
        string message) : Exception(message);

    private sealed class ApiOperationLease(MapRuntimeService owner)
        : IDisposable
    {
        private MapRuntimeService? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ExitApiOperation();
    }
}

/// <summary>Application-lifetime owner so bindings and recognition caches survive navigation.</summary>
public static class MapRuntimeHost
{
    private static MapRuntimeService? _current;

    public static MapRuntimeService Current =>
        _current ?? throw new InvalidOperationException("解锁地图运行服务尚未初始化。");

    public static void Initialize(DispatcherQueue dispatcher)
    {
        _current ??= new MapRuntimeService(dispatcher);
        MapLogCollector.Instance = _current.LogCollector;
    }

    public static void Shutdown()
    {
        var current = Interlocked.Exchange(ref _current, null);
        current?.Dispose();
    }

    public static async Task ShutdownAsync()
    {
        var current = Interlocked.Exchange(ref _current, null);
        if (current is not null)
            await current.DisposeAsync();
    }
}
