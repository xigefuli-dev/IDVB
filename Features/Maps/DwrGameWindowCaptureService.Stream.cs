using Windows.Graphics.Capture;

namespace IDVBuff.Features.Maps;

public sealed partial class DwrGameWindowCaptureService
{
    private readonly object _streamGate = new();
    private GameFrameStream? _stream;
    private IntPtr _streamWindow;
    private MapScreenRect _streamBounds;
    private long _streamVersion;

    public void PrepareViewportCapture()
    {
        if (!TryGetForegroundClientBounds(out var bounds, out var window, out _)) return;
        lock (_streamGate)
        {
            // Cache failures for this window geometry too; do not repeatedly initialize a failed GPU device.
            if (_streamWindow == window && _streamBounds == bounds) return;
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
        lock (_streamGate) { stream = _stream; client = _streamBounds; }
        if (stream is null) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(maximumWait);
        CapturedGameFrame? frame = null;
        try
        {
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
        finally { frame?.Dispose(); }
        return null;
    }

    private void ResetFrameStream()
    {
        lock (_streamGate)
        {
            _stream?.Dispose();
            _stream = null;
            _streamWindow = IntPtr.Zero;
            _streamBounds = default;
            _streamVersion++;
        }
    }
}
