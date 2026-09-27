using Windows.Graphics.Capture;

namespace IDVBuff.Features.Maps;

public sealed partial class DwrGameWindowCaptureService
{
    private readonly object _streamGate = new();
    private GameFrameStream? _stream;
    private IntPtr _streamWindow;
    private MapScreenRect _streamBounds;
    private long _streamVersion;
    private readonly CaptureStreamDemand _streamDemand = new();
    private Timer? _streamIdleTimer;

    public void PrepareViewportCapture()
    {
        if (!TryGetForegroundClientBounds(out var bounds, out var window, out _)) return;
        lock (_streamGate)
        {
            _streamDemand.Request(Environment.TickCount64);
            // Cache failures for this window geometry too; do not repeatedly initialize a failed GPU device.
            if (_streamWindow == window && _streamBounds == bounds) return;
            _streamIdleTimer ??= new Timer(_ => ReleaseIdleFrameStream(), null, 1000, 1000);
            _stream?.Dispose();
            _stream = null;
            _streamWindow = window;
            _streamBounds = bounds;
            var version = ++_streamVersion;
            // Device/session creation is cold work (including driver/JIT startup), never a map-open wait.
            _ = Task.Run(() => InitializeFrameStream(window, version));
        }
    }

    private void InitializeFrameStream(IntPtr window, long version)
    {
        GameFrameStream? created = null;
        try
        {
            if (GraphicsCaptureSession.IsSupported()) created = new GameFrameStream(window);
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
        lock (_streamGate)
        {
            _streamIdleTimer?.Dispose();
            _streamIdleTimer = null;
            _stream?.Dispose();
            _stream = null;
            _streamWindow = IntPtr.Zero;
            _streamBounds = default;
            _streamVersion++;
        }
    }
}
