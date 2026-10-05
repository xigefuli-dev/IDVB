using Windows.Graphics.Capture;

namespace IDVBuff.Features.Maps;

public sealed partial class DwrGameWindowCaptureService
{
    private readonly object _streamGate = new();
    private readonly CaptureDeviceOwner<Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice> _streamDevice =
        new(GameFrameStream.CreateDevice);
    private GameFrameStream? _stream;
    private IntPtr _streamWindow;
    private MapScreenRect _streamBounds;
    private long _streamVersion;
    private readonly CaptureStreamDemand _streamDemand = new();
    private Timer? _streamIdleTimer;

    public void PrepareViewportCapture()
    {
        if (!TryGetForegroundClientBounds(out var bounds, out var window, out _)) return;
        GameFrameStream? previous;
        long version;
        lock (_streamGate)
        {
            _streamDemand.Request(Environment.TickCount64);
            // Cache failures for this window geometry too; do not repeatedly initialize a failed GPU device.
            if (_streamWindow == window && _streamBounds == bounds) return;
            if (_streamIdleTimer is null)
            {
                // This timer outlives the current scan. Do not retain its
                // AsyncLocal frame, operation trace or alignment context.
                if (ExecutionContext.IsFlowSuppressed())
                    _streamIdleTimer = new Timer(_ => ReleaseIdleFrameStream(), null, 1000, 1000);
                else
                {
                    using var flow = ExecutionContext.SuppressFlow();
                    _streamIdleTimer = new Timer(_ => ReleaseIdleFrameStream(), null, 1000, 1000);
                }
            }
            previous = _stream;
            _stream = null;
            _streamWindow = window;
            _streamBounds = bounds;
            version = ++_streamVersion;
        }
        previous?.Dispose();
        // Native creation and shutdown share an owner; neither blocks map-open/UI work.
        _ = CaptureStreamWorker.RunAsync(() => InitializeFrameStream(window, version));
    }

    private void InitializeFrameStream(IntPtr window, long version)
    {
        lock (_streamGate)
            if (_streamVersion != version) return;
        GameFrameStream? created = null;
        try
        {
            if (GraphicsCaptureSession.IsSupported()) created = new GameFrameStream(window, _streamDevice);
            lock (_streamGate)
            {
                if (_streamVersion == version)
                {
                    _stream = created;
                    created = null;
                }
            }
        }
        catch (Exception exception)
        {
            MapLogCollector.Instance.Append(MapLogCategory.ViewportCapture, MapLogLevel.Warning,
                $"新帧捕获初始化失败，回退 GDI · {exception.Message}");
            _streamDevice.Reset();
        }
        finally { created?.Dispose(); }
    }

    public async Task<CapturedGameFrame?> CaptureNextViewportAsync(
        NormalizedRectangle viewport, long afterSystemTicks, TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!viewport.IsValid || maximumWait <= TimeSpan.Zero) return null;
        PrepareViewportCapture();
        GameFrameStream? stream;
        MapScreenRect client;
        lock (_streamGate)
        {
            stream = _stream;
            client = _streamBounds;
            if (stream is not null) _streamDemand.Begin(Environment.TickCount64);
        }
        if (stream is null) return null;
        CapturedGameFrame? frame = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(maximumWait);
            frame = await stream.CaptureAsync(client, GetViewportBounds(client, viewport),
                afterSystemTicks, deadline.Token).ConfigureAwait(false);
            if (frame is null)
            {
                lock (_streamGate)
                {
                    if (ReferenceEquals(_stream, stream))
                    {
                        _stream.Dispose();
                        _stream = null;
                    }
                }
                return null;
            }
            if (frame is not null
                && TryGetForegroundClientBounds(out var currentBounds, out var currentWindow, out _)
                && currentWindow == frame.WindowHandle && currentBounds == client)
            {
                lock (_streamGate)
                {
                    if (ReferenceEquals(_stream, stream))
                    {
                        var result = frame;
                        frame = null;
                        return result;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            MapLogCollector.Instance.Append(MapLogCategory.ViewportCapture, MapLogLevel.Warning,
                $"新帧捕获失败，回退 GDI · {exception.Message}");
            lock (_streamGate)
            {
                if (ReferenceEquals(_stream, stream))
                {
                    _stream.Dispose();
                    _stream = null;
                }
            }
        }
        finally
        {
            try { frame?.Dispose(); }
            finally { lock (_streamGate) _streamDemand.End(Environment.TickCount64); }
        }
        return null;
    }

    private void ReleaseIdleFrameStream()
    {
        GameFrameStream? idle;
        lock (_streamGate)
        {
            // An open-map alignment, tracker or enabled native mini-map keeps
            // making requests. With no consumer, WGC must not capture gameplay
            // indefinitely just to keep a warm pool for a future map opening.
            if (_stream is null || !_streamDemand.CanRelease(Environment.TickCount64)) return;
            idle = _stream;
            _stream = null;
            _streamWindow = IntPtr.Zero;
            _streamBounds = default;
            _streamVersion++;
            _streamIdleTimer?.Dispose();
            _streamIdleTimer = null;
        }
        // Native shutdown can wait for callbacks: keep it off the UI thread
        // and outside the service gate so a new request can create its own pool.
        try
        {
            idle.Dispose();
            MapLogCollector.Instance.Append(MapLogCategory.ViewportCapture, MapLogLevel.Info,
                "WGC idle capture stopped · no pending requests for at least 2000ms");
        }
        catch (Exception exception)
        {
            MapLogCollector.Instance.Append(MapLogCategory.ViewportCapture, MapLogLevel.Warning,
                $"WGC idle capture shutdown failed · {exception.Message}");
        }
    }

    private void ResetFrameStream()
    {
        GameFrameStream? previous;
        lock (_streamGate)
        {
            _streamIdleTimer?.Dispose();
            _streamIdleTimer = null;
            previous = _stream;
            _stream = null;
            _streamWindow = IntPtr.Zero;
            _streamBounds = default;
            _streamVersion++;
            // Queue retirement while holding the version gate: a subsequent
            // match cannot enqueue acquisition ahead of this retirement.
            _ = CaptureStreamWorker.RunAsync(() =>
            {
                using var memory = IDVBuff.Diagnostics.RealtimePerformanceTracker.TrackScope(
                    "WGC.MatchDeviceReset", forceLog: true);
                try { previous?.Dispose(); }
                finally { _streamDevice.Reset(); }
            });
        }
    }
}
