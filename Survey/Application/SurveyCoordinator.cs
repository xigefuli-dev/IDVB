using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Survey.Application;

public sealed partial class SurveyCoordinator : ISurveyCoordinator
{
    private readonly ISurveyProjectRepository _projects;
    private readonly ISurveyAssetStore _assets;
    private readonly ISurveyPreprocessor? _preprocessor;
    private readonly ISurveyPairRegistrar? _registrar;
    private readonly IPoseGraphOptimizer? _poseGraph;
    private readonly SurveyRegistrationTuning _registrationTuning;
    private readonly ISurveyVisualComposer? _visualComposer;
    private readonly ISurveyStructureFusion? _structureFusion;
    private readonly ISurveyLayerRasterEditor? _rasterEditor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _activeProjectId;
    private Guid? _activeMatchId;
    private long _activeOperationEpoch;
    private bool _initialized;
    private bool _disposed;

    public SurveyCoordinator(
        ISurveyProjectRepository projects,
        ISurveyAssetStore assets)
        : this(
            projects,
            assets,
            null,
            null,
            null,
            new SurveyRegistrationTuning())
    {
    }

    public SurveyCoordinator(
        ISurveyProjectRepository projects,
        ISurveyAssetStore assets,
        ISurveyPreprocessor? preprocessor,
        ISurveyPairRegistrar? registrar,
        IPoseGraphOptimizer? poseGraph,
        SurveyRegistrationTuning registrationTuning)
        : this(
            projects,
            assets,
            preprocessor,
            registrar,
            poseGraph,
            registrationTuning,
            null,
            null,
            null)
    {
    }

    public SurveyCoordinator(
        ISurveyProjectRepository projects,
        ISurveyAssetStore assets,
        ISurveyPreprocessor? preprocessor,
        ISurveyPairRegistrar? registrar,
        IPoseGraphOptimizer? poseGraph,
        SurveyRegistrationTuning registrationTuning,
        ISurveyVisualComposer? visualComposer,
        ISurveyStructureFusion? structureFusion,
        ISurveyLayerRasterEditor? rasterEditor = null)
    {
        _projects = projects;
        _assets = assets;
        _preprocessor = preprocessor;
        _registrar = registrar;
        _poseGraph = poseGraph;
        _registrationTuning = registrationTuning;
        _visualComposer = visualComposer;
        _structureFusion = structureFusion;
        _rasterEditor = rasterEditor;
        _registrationTuning.Validate();
    }

    public Guid? ArmedResumeProjectId { get; private set; }
    public SurveyStatusSnapshot Status { get; private set; } = SurveyStatusSnapshot.Inactive;

    public event EventHandler<SurveyStatusSnapshot>? StatusChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_initialized)
            return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            await _projects.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ArmResumeAsync(
        Guid? projectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (projectId is { } id)
        {
            var project = await _projects.GetAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(id);
            if (project.Project.State is SurveyProjectState.Archived or SurveyProjectState.Published)
                throw new InvalidOperationException("Only unfinished survey projects can be resumed.");
        }
        ArmedResumeProjectId = projectId;
    }

    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> StartAsync(
        SurveyStartRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetStatus(Status with
            {
                RuntimeState = SurveyRuntimeState.Activating,
                LastErrorCode = SurveyErrorCode.None,
                LastMessage = "正在启动测绘模式…"
            });

            var resumeId = request.ResumeProjectId ?? ArmedResumeProjectId;
            SurveyProjectSnapshot snapshot;
            if (resumeId is { } projectId)
            {
                snapshot = await _projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false)
                    ?? throw new SurveyProjectNotFoundException(projectId);
                if (snapshot.Project.State is SurveyProjectState.Archived or SurveyProjectState.Published)
                {
                    return Failure<SurveyProjectSnapshot>(
                        SurveyErrorCode.ProjectArchived,
                        "该测绘项目不可继续采集。");
                }

                snapshot = await _projects.SetProjectStateAsync(
                    new SurveyProjectStateRequest(
                        request.CommandId,
                        projectId,
                        snapshot.Project.Revision,
                        SurveyProjectState.Collecting),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                snapshot = await _projects.CreateAsync(request, cancellationToken).ConfigureAwait(false);
            }

            ArmedResumeProjectId = null;
            _activeProjectId = snapshot.Project.ProjectId;
            _activeMatchId = request.MatchId;
            _activeOperationEpoch = request.OperationEpoch;
            SetStatus(CreateStatus(
                snapshot,
                SurveyRuntimeState.WaitingForMapOpen,
                "测绘模式已启动，等待地图打开。"));
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.Cancelled, "测绘启动已取消。");
        }
        catch (SurveyProjectNotFoundException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.ProjectNotFound, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyProjectSnapshot>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyObservationCommitResult>> AddObservationAsync(
        SurveyObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ValidateActiveRequest(request.ProjectId, request.Frame.Capture))
            {
                return Failure<SurveyObservationCommitResult>(
                    SurveyErrorCode.InvalidState,
                    "该地图帧不属于当前测绘对局。");
            }
            if (request.Frame.Bytes.IsEmpty
                || request.Frame.PixelWidth <= 0
                || request.Frame.PixelHeight <= 0)
            {
                return Failure<SurveyObservationCommitResult>(
                    SurveyErrorCode.FrameInvalid,
                    "地图帧内容无效。");
            }

            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            SetStatus(CreateStatus(
                current,
                SurveyRuntimeState.ProcessingObservation,
                "正在保存测绘图层…",
                isSaving: true));

            var asset = await _assets.PutAsync(
                request.ProjectId,
                request.Frame,
                cancellationToken).ConfigureAwait(false);
            var (observation, layer) = BuildObservationAndLayer(
                current, request.Frame, asset, request.ProjectId, null);

            SetStatus(CreateStatus(
                current,
                SurveyRuntimeState.Committing,
                "正在提交测绘图层…",
                isSaving: true));
            var committed = await _projects.CommitObservationAsync(
                observation,
                layer,
                request.ExpectedRevision,
                request.CommandId,
                cancellationToken).ConfigureAwait(false);
            SetStatus(CreateStatus(
                committed.Snapshot,
                SurveyRuntimeState.WaitingForNextOpen,
                committed.WasAlreadyCommitted
                    ? "该画面已经记录。"
                    : $"已采集第 {committed.Snapshot.Observations.Count} 个原始测绘图层。",
                lastCaptureAt: request.Frame.Capture.CapturedAt));
            return SurveyOperationResult<SurveyObservationCommitResult>.Success(committed);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyObservationCommitResult>(
                SurveyErrorCode.RevisionConflict,
                exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<SurveyObservationCommitResult>(
                SurveyErrorCode.Cancelled,
                "本次测绘采集已取消。");
        }
        catch (Exception exception)
        {
            return Fault<SurveyObservationCommitResult>(SurveyErrorCode.AssetWriteFailed, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyObservationCommitResult>> ImportObservationAsync(
        SurveyObservationImportRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.Frame.Bytes.IsEmpty
                || request.Frame.PixelWidth <= 0
                || request.Frame.PixelHeight <= 0)
            {
                return Failure<SurveyObservationCommitResult>(
                    SurveyErrorCode.FrameInvalid,
                    "导入的图片内容无效。");
            }

            var current = await _projects.GetAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.State == SurveyProjectState.Archived)
            {
                return Failure<SurveyObservationCommitResult>(
                    SurveyErrorCode.ProjectArchived,
                    "已归档的测绘项目为只读。");
            }

            var asset = await _assets.PutAsync(
                request.ProjectId,
                request.Frame,
                cancellationToken).ConfigureAwait(false);
            var (observation, layer) = BuildObservationAndLayer(
                current, request.Frame, asset, request.ProjectId, request.LayerName);
            var committed = await _projects.CommitObservationAsync(
                observation,
                layer,
                request.ExpectedRevision,
                request.CommandId,
                cancellationToken).ConfigureAwait(false);
            if (!committed.WasAlreadyCommitted)
            {
                committed = IsRootLayer(committed)
                    ? await ProcessRootObservationAsync(committed, cancellationToken).ConfigureAwait(false)
                    : await ProcessObservationAsync(committed, cancellationToken).ConfigureAwait(false);
            }

            // 导入是编辑器中的手动编辑，不切换对局会话的状态机；
            // 保留当前 RuntimeState，只更新图层计数与提示消息。
            SetStatus(CreateStatus(
                committed.Snapshot,
                Status.RuntimeState,
                committed.WasAlreadyCommitted
                    ? "该图片已经导入到当前楼层。"
                    : $"已导入“{committed.Layer.Name}”。"));
            return SurveyOperationResult<SurveyObservationCommitResult>.Success(committed);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyObservationCommitResult>(
                SurveyErrorCode.RevisionConflict,
                exception.Message);
        }
        catch (SurveyProjectNotFoundException exception)
        {
            return Failure<SurveyObservationCommitResult>(
                SurveyErrorCode.ProjectNotFound,
                exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure<SurveyObservationCommitResult>(
                SurveyErrorCode.Cancelled,
                "本次图片导入已取消。");
        }
        catch (Exception exception)
        {
            // 刻意用 Failure 而非 Fault：Fault 会把 RuntimeState 置为 Faulted，
            // 打挂正在进行的游戏采集会话。导入是编辑器被动编辑，不应影响对局。
            return Failure<SurveyObservationCommitResult>(
                SurveyErrorCode.AssetWriteFailed,
                exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SurveyOperationResult<SurveyProjectSnapshot>> EditLayerAsync(
        SurveyLayerEditRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _projects.GetAsync(request.ProjectId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new SurveyProjectNotFoundException(request.ProjectId);
            if (current.Project.State == SurveyProjectState.Archived)
            {
                return Failure<SurveyProjectSnapshot>(
                    SurveyErrorCode.ProjectArchived,
                    "已归档的测绘项目为只读；请先复制项目再编辑。");
            }
            var snapshot = await _projects.EditLayerAsync(request, cancellationToken).ConfigureAwait(false);
            SetStatus(CreateStatus(
                snapshot,
                Status.RuntimeState,
                "图层修改已保存。"));
            return SurveyOperationResult<SurveyProjectSnapshot>.Success(snapshot);
        }
        catch (SurveyRevisionConflictException exception)
        {
            return Failure<SurveyProjectSnapshot>(SurveyErrorCode.RevisionConflict, exception.Message);
        }
        catch (Exception exception)
        {
            return Fault<SurveyProjectSnapshot>(SurveyErrorCode.StorageUnavailable, exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool ValidateActiveRequest(Guid projectId, SurveyCaptureContext capture) =>
        _activeProjectId == projectId
        && _activeMatchId == capture.MatchId
        && _activeOperationEpoch == capture.OperationEpoch
        && Status.RuntimeState is not SurveyRuntimeState.Paused
        && Status.RuntimeState is not SurveyRuntimeState.Ending
        && Status.RuntimeState is not SurveyRuntimeState.Faulted;

    private static string CreateIdempotencyKey(
        SurveyCaptureContext capture,
        string digest) =>
        $"{capture.MatchId:N}:{capture.MapToggleVersion}:{capture.FloorKey.ToLowerInvariant()}:{digest}";

    private static (SurveyObservation Observation, SurveyMapLayer Layer) BuildObservationAndLayer(
        SurveyProjectSnapshot current,
        SurveyEncodedFrame frame,
        SurveyAssetReference asset,
        Guid projectId,
        string? layerName)
    {
        var floor = current.Floors.FirstOrDefault(candidate =>
            string.Equals(
                candidate.FloorKey,
                frame.Capture.FloorKey,
                StringComparison.OrdinalIgnoreCase));
        var floorId = floor?.FloorId ?? Guid.NewGuid();
        var observationId = Guid.NewGuid();
        var layerId = Guid.NewGuid();
        var isRoot = current.Layers.All(layer => layer.IsDeleted);
        var state = isRoot
            ? SurveyObservationState.Registered
            : SurveyObservationState.Unregistered;
        var idempotencyKey = CreateIdempotencyKey(frame.Capture, asset.Sha256);
        var observation = new SurveyObservation(
            observationId,
            projectId,
            floorId,
            idempotencyKey,
            frame.Capture,
            asset,
            state,
            isRoot ? 1d : 0d,
            isRoot ? SurveyErrorCode.None : SurveyErrorCode.RegistrationRejected,
            isRoot ? null : "尚未执行自动配准，可在编辑器中手动对齐。",
            null,
            null);
        var layer = new SurveyMapLayer(
            layerId,
            projectId,
            floorId,
            observationId,
            string.IsNullOrWhiteSpace(layerName)
                ? $"测绘图层 {current.Layers.Count + 1}"
                : layerName.Trim(),
            current.Layers.Count == 0 ? 0 : current.Layers.Max(candidate => candidate.ZOrder) + 1,
            true,
            false,
            false,
            1d,
            SurveyBlendMode.Normal,
            SurveyLayerTransform.Identity,
            null,
            current.Project.Revision,
            0);
        return (observation, layer);
    }

    private static bool IsRootLayer(SurveyObservationCommitResult committed) =>
        committed.Snapshot.Layers.Count(item => !item.IsDeleted) == 1;

    private SurveyOperationResult<T> Failure<T>(SurveyErrorCode code, string message)
    {
        SetStatus(Status with
        {
            LastErrorCode = code,
            LastMessage = message,
            IsSaving = false
        });
        return SurveyOperationResult<T>.Failure(code, message);
    }

    private SurveyOperationResult<T> Fault<T>(SurveyErrorCode code, Exception exception)
    {
        var diagnosticId = Guid.NewGuid().ToString("N");
        SetStatus(Status with
        {
            RuntimeState = SurveyRuntimeState.Faulted,
            LastErrorCode = code,
            LastMessage = exception.Message,
            DiagnosticId = diagnosticId,
            IsSaving = false
        });
        return SurveyOperationResult<T>.Failure(code, exception.Message, diagnosticId);
    }
}
