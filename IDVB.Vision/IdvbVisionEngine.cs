using System.Diagnostics;
using IDVBuff.Features.Maps;
using OpenCvSharp;

namespace IdentityVisionBridge.Vision;

/// <summary>Uses the production algorithms with an explicit map repository and no desktop app dependency.</summary>
public sealed class IdvbVisionEngine : IIdvbVisionEngine
{
    private readonly MapRepository _repository;
    private readonly string _cacheDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MapCvRecognitionService? _recognition;
    private bool _disposed;

    public IdvbVisionEngine(string mapDirectory, string? cacheDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapDirectory);
        _repository = new MapRepository(Path.GetFullPath(mapDirectory));
        _cacheDirectory = cacheDirectory is null
            ? Path.Combine(IDVBuff.AppDataPaths.RootDirectory, Guid.NewGuid().ToString("N"))
            : Path.GetFullPath(cacheDirectory);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await PrepareAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _recognition ??= new MapCvRecognitionService(_repository, dataDirectory: _cacheDirectory);
        if (_recognition.CatalogRevision != _repository.GetCatalogRevision() || _recognition.TotalMapCount == 0)
            await _recognition.RefreshCacheAsync().ConfigureAwait(false);
        // BuildingCount is zero during the desktop startup delay, before workers enter the registry.
        await _recognition.WaitForVpsg3PreparationAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<IReadOnlyList<VisionMap>> GetMapsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            var maps = await _repository.GetMapsAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return maps.Select(map => new VisionMap(map.Id, map.DisplayName, map.Class,
                Array.AsReadOnly(map.Floors.Select(floor => floor.Key).ToArray()))).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<VisionScanResult> ScanAsync(VisionScanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Mode)) throw new ArgumentOutOfRangeException(nameof(request.Mode));
        var frameInput = CopyFrame(request.Frame);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            // Fresh observations and floor-specific neutral seeds; no cross-request learned scale.
            _recognition!.ResetMatchState();
            return await Task.Run(() =>
            {
                var startedTimestamp = Stopwatch.GetTimestamp();
                using var frame = DecodeFrame(frameInput);
                return _recognition.ScanForApi(frame, request.MapClass, (ScanPerformanceMode)request.Mode, cancellationToken, startedTimestamp);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<VisionAlignmentResult> AlignAsync(VisionAlignmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MapId == Guid.Empty) throw new ArgumentException("MapId is empty.", nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FloorKey);
        if (request.BudgetMilliseconds is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(request.BudgetMilliseconds));
        var frameInput = CopyFrame(request.Frame);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await PrepareAsync(cancellationToken).ConfigureAwait(false);
            _recognition!.ResetMatchState();
            return await Task.Run(() =>
            {
                var operationId = Guid.NewGuid().ToString("N");
                var timer = Stopwatch.StartNew();
                using var frame = DecodeFrame(frameInput);
                var map = _recognition.TryGetMap(request.MapId);
                var floor = map is null ? null : map.Floors.Select(floor => floor.Key)
                    .FirstOrDefault(key => string.Equals(key, request.FloorKey, StringComparison.OrdinalIgnoreCase));
                if (map is null || floor is null)
                    return new VisionAlignmentResult { OperationId = operationId, Outcome = VisionOutcome.Rejected,
                        Reason = "map-or-floor-not-found", MapId = request.MapId, FloorKey = request.FloorKey };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(request.BudgetMilliseconds);
                using var budget = MapNoDoorAlignmentBudgetContext.Enter(() => deadline.IsCancellationRequested
                    ? 0 : Math.Max(0, request.BudgetMilliseconds - (int)Math.Ceiling(timer.Elapsed.TotalMilliseconds)));
                var tuning = MapAlignmentChannelRegistry.Resolve(map, floor).Channel == MapAlignmentChannel.LowStructure
                    ? MapAlignmentChannelRegistry.CreateLowStructure() : new MapStructureRegistrationTuning();
                tuning.StructureFallbackBudgetMilliseconds = request.BudgetMilliseconds;
                tuning.EnforceTimeBudget = true;
                tuning.Normalize();
                var attempt = _recognition.TryAlignWithVpsg3(frame, map, floor, 1, out var fast)
                    ? fast : _recognition.AlignFloorWithoutGates(frame, map.Id, floor,
                        MapFloorScaleSeedRules.CreateIndependentFloorSeed(map, floor), MapOverlayAlignmentMode.Uniform,
                        new MapRecognitionTuning(), tuning, allowPrimaryFloor: true);
                cancellationToken.ThrowIfCancellationRequested();
                var recognition = attempt.StructureAccepted ? attempt.Recognition : null;
                var transform = recognition?.Result.OverlayTransform;
                var timedOut = deadline.IsCancellationRequested || timer.Elapsed.TotalMilliseconds > request.BudgetMilliseconds;
                return new VisionAlignmentResult
                {
                    OperationId = operationId,
                    Outcome = timedOut ? VisionOutcome.TimedOut : transform is not null ? VisionOutcome.Succeeded : VisionOutcome.Rejected,
                    MapId = map.Id, FloorKey = floor,
                    Transform = timedOut ? null : VisionApiMapper.Transform(transform),
                    Confidence = timedOut ? 0 : recognition?.Result.LocalizationConfidence ?? 0,
                    Reason = timedOut ? "deadline-exceeded" : attempt.FailureReason,
                    ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static VisionFrame CopyFrame(VisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.EncodedImage);
        if (frame.EncodedImage.Length is 0 or > 33554432) throw new ArgumentException("Encoded image must be 1 byte to 32 MiB.", nameof(frame));
        return frame with { EncodedImage = (byte[])frame.EncodedImage.Clone() };
    }

    private static CapturedGameFrame DecodeFrame(VisionFrame frame)
    {
        var image = Cv2.ImDecode(frame.EncodedImage, ImreadModes.Color);
        try
        {
            if (image.Empty() || image.Width > 8192 || image.Height > 8192)
                throw new ArgumentException("Invalid image or image dimensions exceed 8192 pixels.", nameof(frame));
            var viewport = frame.Viewport ?? new(0, 0, image.Width, image.Height);
            var clientWidth = frame.ClientWidth ?? image.Width;
            var clientHeight = frame.ClientHeight ?? image.Height;
            if (!double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) || viewport.X < 0 || viewport.Y < 0
                || viewport.Width != image.Width || viewport.Height != image.Height
                || viewport.X + viewport.Width > clientWidth || viewport.Y + viewport.Height > clientHeight)
                throw new ArgumentException("Viewport image and client pixel bounds do not match.", nameof(frame));
            return new(image, new(0, 0, clientWidth, clientHeight),
                new(viewport.X, viewport.Y, viewport.Width, viewport.Height), IntPtr.Zero) { CaptureBackend = "embedded-image" };
        }
        catch { image.Dispose(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_recognition is { } recognition)
            {
                recognition.Dispose();
                try { await recognition.WaitForVpsg3PreparationAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _recognition = null;
        }
        finally { _gate.Release(); }
    }
}
