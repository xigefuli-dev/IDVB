using System.Runtime.InteropServices;
using OpenCvSharp;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace IDVBuff.Features.Maps;

internal sealed partial class GameFrameStream
{
    private readonly object _readbackGate = new();
    private ViewportReadback? _readback;

    private Mat ReadViewport(IDirect3DSurface surface, Rect roi)
    {
        lock (_readbackGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _resourcesDisposed) != 0, this);
            _readback ??= new ViewportReadback(_device);
            return _readback.Read(surface, roi);
        }
    }

    /// <summary>One reusable D3D11 staging texture; copies only the calibrated ROI to CPU memory.</summary>
    private sealed class ViewportReadback : IDisposable
    {
        private readonly IntPtr _device;
        private readonly IntPtr _context;
        private readonly CreateTexture _createTexture;
        private readonly CopyRegion _copyRegion;
        private readonly MapResource _map;
        private readonly UnmapResource _unmap;
        private IntPtr _staging;
        private Size _size;

        public ViewportReadback(IDirect3DDevice device)
        {
            var iid = new Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140");
            _device = device.As<IDxgiInterfaceAccess>().GetInterface(ref iid);
            try
            {
                Method<GetContext>(_device, 40)(_device, out _context);
                _createTexture = Method<CreateTexture>(_device, 5);
                _copyRegion = Method<CopyRegion>(_context, 46);
                _map = Method<MapResource>(_context, 14);
                _unmap = Method<UnmapResource>(_context, 15);
            }
            catch { Dispose(); throw; }
        }

        public Mat Read(IDirect3DSurface surface, Rect roi)
        {
            if (_staging == IntPtr.Zero || _size != roi.Size)
            {
                if (_staging != IntPtr.Zero) Marshal.Release(_staging);
                _staging = IntPtr.Zero;
                var description = new TextureDescription
                {
                    Width = (uint)roi.Width, Height = (uint)roi.Height,
                    MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1,
                    Usage = 3, CpuAccessFlags = 0x20000
                };
                Marshal.ThrowExceptionForHR(_createTexture(_device, in description, IntPtr.Zero, out _staging));
                _size = roi.Size;
            }
            var iid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            var source = surface.As<IDxgiInterfaceAccess>().GetInterface(ref iid);
            try
            {
                var box = new SourceBox
                {
                    Left = (uint)roi.X, Top = (uint)roi.Y,
                    Right = (uint)roi.Right, Bottom = (uint)roi.Bottom, Back = 1
                };
                _copyRegion(_context, _staging, 0, 0, 0, 0, source, 0, in box);
                Marshal.ThrowExceptionForHR(_map(_context, _staging, 0, 1, 0, out var mapped));
                try
                {
                    if (mapped.Data == IntPtr.Zero || mapped.RowPitch < roi.Width * 4L)
                        throw new InvalidDataException("Invalid GPU viewport row pitch.");
                    using var pixels = Mat.FromPixelData(roi.Height, roi.Width, MatType.CV_8UC4,
                        mapped.Data, mapped.RowPitch);
                    return pixels.Clone();
                }
                finally { _unmap(_context, _staging, 0); }
            }
            finally { Marshal.Release(source); }
        }

        public void Dispose()
        {
            if (_staging != IntPtr.Zero) Marshal.Release(_staging);
            if (_context != IntPtr.Zero) Marshal.Release(_context);
            if (_device != IntPtr.Zero) Marshal.Release(_device);
        }

        // Slots follow the Windows SDK d3d11.h IUnknown-derived vtables (no external DirectX wrapper).
        private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(
                Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        [StructLayout(LayoutKind.Sequential)]
        private struct TextureDescription
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality;
            public uint Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct SourceBox { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MappedResource { public IntPtr Data; public uint RowPitch, DepthPitch; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void GetContext(IntPtr self, out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateTexture(IntPtr self, in TextureDescription description,
            IntPtr initialData, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void CopyRegion(IntPtr self, IntPtr destination, uint destinationSubresource,
            uint x, uint y, uint z, IntPtr source, uint sourceSubresource, in SourceBox box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MapResource(IntPtr self, IntPtr resource, uint subresource,
            uint mapType, uint flags, out MappedResource mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UnmapResource(IntPtr self, IntPtr resource, uint subresource);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }
}
