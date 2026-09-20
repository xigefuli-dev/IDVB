using System.Runtime.InteropServices;
using OpenCvSharp;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace IDVBuff.Features.Maps;

/// <summary>A warm GPU frame pool. CPU readback occurs only on demand.</summary>
internal sealed partial class GameFrameStream : IDisposable
{
    private readonly object _gate = new();
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private Direct3D11CaptureFrame? _latest;
    private TaskCompletionSource _changed = NewSignal();
    private bool _disposed;
    private int _resourcesDisposed;

    public GameFrameStream(IntPtr window)
    {
        Window = window;
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var pointer = GraphicsCaptureItem.As<ICaptureItemInterop>().CreateForWindow(window, ref iid);
        GraphicsCaptureItem item;
        try { item = MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
        _device = CreateDevice();
        try
        {
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                _session = _pool.CreateCaptureSession(item);
                try
                {
                    if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                        _session.IsCursorCaptureEnabled = false;
                    TryDisableBorder(_session);
                    _pool.FrameArrived += OnFrameArrived;
                    _session.StartCapture();
                }
                catch { _session.Dispose(); throw; }
            }
            catch { _pool.Dispose(); throw; }
        }
        catch { _device.Dispose(); throw; }
    }

    public IntPtr Window { get; }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                var newer = sender.TryGetNextFrame();
                if (newer is not null) { frame.Dispose(); frame = newer; }
                _latest?.Dispose();
                _latest = frame;
                var changed = _changed;
                _changed = NewSignal();
                changed.TrySetResult();
            }
            catch (Exception)
            {
                // Device loss wakes the consumer; the capture service falls back to GDI.
                _disposed = true;
                _changed.TrySetResult();
            }
        }
    }

    public async Task<CapturedGameFrame?> CaptureAsync(
        MapScreenRect client, MapScreenRect viewport, long afterSystemTicks,
        CancellationToken cancellationToken)
    {
        Direct3D11CaptureFrame? frame;
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_disposed) return null;
                frame = _latest;
                _latest = null;
                changed = _changed.Task;
            }
            if (frame is not null)
            {
                if (frame.SystemRelativeTime.Ticks > afterSystemTicks) break;
                frame.Dispose();
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        using (frame)
        {
            // WGC includes the visible window frame; calibrated ROI coordinates are screen based.
            Marshal.ThrowExceptionForHR(DwmGetWindowAttribute(Window, 9, out var bounds, 16));
            if (bounds.Right - bounds.Left != frame.ContentSize.Width
                || bounds.Bottom - bounds.Top != frame.ContentSize.Height)
                return null;
            var roi = new Rect((int)viewport.X - bounds.Left, (int)viewport.Y - bounds.Top,
                (int)viewport.Width, (int)viewport.Height);
            if (roi.X < 0 || roi.Y < 0 || roi.Right > frame.ContentSize.Width
                || roi.Bottom > frame.ContentSize.Height)
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            return new CapturedGameFrame(ReadViewport(frame.Surface, roi), client, viewport, Window)
            {
                CaptureSystemRelativeTicks = frame.SystemRelativeTime.Ticks,
                CaptureBackend = "wgc-frame-arrived"
            };
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0) return;
        lock (_gate)
        {
            _disposed = true;
            _pool.FrameArrived -= OnFrameArrived;
            _latest?.Dispose();
            _latest = null;
            _changed.TrySetResult();
        }
        // Do not hold the callback lock while closing the native frame pool.
        _session.Dispose();
        _pool.Dispose();
        lock (_readbackGate)
        {
            _readback?.Dispose();
            _readback = null;
            _device.Dispose();
        }
    }

    private static IDirect3DDevice CreateDevice()
    {
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, dxgi = IntPtr.Zero,
            inspectable = IntPtr.Zero, multithread = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero,
                0x20, IntPtr.Zero, 0, 7, out device, out _, out context));
            // The WGC worker and ROI readback share this device's immediate context.
            var multithreadId = new Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(context, in multithreadId, out multithread));
            var protect = Marshal.GetDelegateForFunctionPointer<SetMultithreadProtected>(
                Marshal.ReadIntPtr(Marshal.ReadIntPtr(multithread), 5 * IntPtr.Size));
            protect(multithread, 1);
            var iid = new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            if (inspectable != IntPtr.Zero) Marshal.Release(inspectable);
            if (multithread != IntPtr.Zero) Marshal.Release(multithread);
            if (dxgi != IntPtr.Zero) Marshal.Release(dxgi);
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (device != IntPtr.Zero) Marshal.Release(device);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetMultithreadProtected(IntPtr self, int enabled);

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureSession3Native
    {
        int GetIids(out uint iidCount, out IntPtr iids);
        int GetRuntimeClassName(out IntPtr className);
        int GetTrustLevel(out int trustLevel);
        int GetIsBorderRequired(out byte isBorderRequired);
        int PutIsBorderRequired(byte isBorderRequired);
    }

    private static void TryDisableBorder(GraphicsCaptureSession session)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;
        try
        {
            var native = session.As<IGraphicsCaptureSession3Native>();
            native.PutIsBorderRequired(0);
        }
        catch
        {
            // Silently ignore on platforms without IGraphicsCaptureSession3.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr device);
}
