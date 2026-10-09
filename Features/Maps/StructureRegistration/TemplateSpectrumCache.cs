using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal readonly record struct TemplateSpectrumCacheKey(
    Size ResizeTargetSize,
    int BoundsX,
    int BoundsY,
    int BoundsWidth,
    int BoundsHeight,
    int VisibleMaskErodePixels,
    int Factor,
    VisibleAwareCorrelationMode RequestedBackend,
    string ActualBackend,
    Size DftSize,
    Size TemplateSize);

internal readonly record struct TemplateSpectrumCacheStatus(
    string Outcome,
    int RetainedEntries,
    long RetainedBytes)
{
    internal string ToDiagnostic(Size dftSize) =>
        $"{Outcome}/entries={RetainedEntries}/retainedBytes={RetainedBytes}"
        + $"/dft={dftSize.Width}x{dftSize.Height}";
}

internal sealed class TemplateSpectrumCache : IDisposable
{
    // Bound one frame's matrix payload to 32 MiB and 24 DFT shapes.
    private const int MaximumEntryCount = 24;
    private const long MaximumRetainedBytes = 32L * 1024L * 1024L;
    private readonly object _sync = new();
    private readonly Dictionary<TemplateSpectrumCacheKey, TemplateSpectraPair> _entries = new();
    private long _retainedBytes;
    private bool _disposed;

    internal bool TryAcquire(
        TemplateSpectrumCacheKey key,
        Mat structure,
        Mat visible,
        out Lease? lease,
        out TemplateSpectrumCacheStatus status)
    {
        lock (_sync)
        {
            lease = null;
            if (_disposed)
            {
                status = CurrentStatus("frame-cache-disposed");
                return false;
            }
            if (_entries.TryGetValue(key, out var existing))
            {
                lease = existing.AcquireLease("frame-hit");
                status = CurrentStatus("frame-hit");
                return true;
            }

            var bytes = TemplateSpectraPair.EstimateBytes(key.DftSize);
            if (_entries.Count >= MaximumEntryCount
                || bytes > MaximumRetainedBytes - _retainedBytes)
            {
                status = CurrentStatus("capacity-bypass");
                return false;
            }

            var created = TemplateSpectraPair.Create(key.DftSize, structure, visible);
            Lease? createdLease = null;
            try
            {
                createdLease = created.AcquireLease("frame-miss");
                _entries.Add(key, created);
                _retainedBytes += bytes;
                lease = createdLease;
                status = CurrentStatus("frame-miss");
                return true;
            }
            catch
            {
                createdLease?.Dispose();
                created.ReleaseCacheOwner();
                throw;
            }
        }
    }

    private TemplateSpectrumCacheStatus CurrentStatus(string outcome) =>
        new(outcome, _entries.Count, _retainedBytes);

    public void Dispose()
    {
        TemplateSpectraPair[] entries;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            entries = _entries.Values.ToArray();
            _entries.Clear();
            _retainedBytes = 0;
        }
        foreach (var entry in entries)
            entry.ReleaseCacheOwner();
    }

    internal sealed class Lease : IDisposable
    {
        private TemplateSpectraPair? _pair;

        internal Lease(TemplateSpectraPair pair, string cacheOutcome)
        {
            _pair = pair;
            CacheOutcome = cacheOutcome;
        }

        internal string CacheOutcome { get; }
        internal Mat StructureSpectrum =>
            (_pair ?? throw new ObjectDisposedException(nameof(Lease))).StructureSpectrum;
        internal Mat VisibleSpectrum =>
            (_pair ?? throw new ObjectDisposedException(nameof(Lease))).VisibleSpectrum;

        public void Dispose() =>
            System.Threading.Interlocked.Exchange(ref _pair, null)?.ReleaseLease();
    }

    internal sealed class TemplateSpectraPair
    {
        private readonly object _sync = new();
        private Padded? _structure;
        private Padded? _visible;
        private int _leaseCount;
        private bool _cacheOwnerAlive = true;
        private bool _released;

        private TemplateSpectraPair(Padded structure, Padded visible)
        {
            _structure = structure;
            _visible = visible;
        }

        internal Mat StructureSpectrum
        {
            get
            {
                lock (_sync)
                    return _structure?.Value
                        ?? throw new ObjectDisposedException(nameof(TemplateSpectraPair));
            }
        }

        internal Mat VisibleSpectrum
        {
            get
            {
                lock (_sync)
                    return _visible?.Value
                        ?? throw new ObjectDisposedException(nameof(TemplateSpectraPair));
            }
        }

        internal static long EstimateBytes(Size size)
        {
            // Two CV_64FC1 spectra, each backed by height * (width + 1) values.
            try { return checked(16L * size.Height * (size.Width + 1L)); }
            catch (OverflowException) { return long.MaxValue; }
        }

        internal static TemplateSpectraPair Create(
            Size size,
            Mat structure,
            Mat visible)
        {
            Padded? structureSpectrum = null;
            Padded? visibleSpectrum = null;
            try
            {
                structureSpectrum = Spectrum(size, structure);
                visibleSpectrum = Spectrum(size, visible);
                return new TemplateSpectraPair(structureSpectrum, visibleSpectrum);
            }
            catch
            {
                visibleSpectrum?.Dispose();
                structureSpectrum?.Dispose();
                throw;
            }
        }

        internal static Lease CreateLocalLease(
            Size size,
            Mat structure,
            Mat visible,
            string fallbackReason)
        {
            var pair = Create(size, structure, visible);
            try { return pair.AcquireLocalLease($"local/{fallbackReason}"); }
            catch
            {
                pair.ReleaseCacheOwner();
                throw;
            }
        }

        private static Padded Spectrum(Size size, Mat template)
        {
            var spectrum = new Padded(size);
            try
            {
                using (var destination = new Mat(spectrum.Value,
                    new Rect(0, 0, template.Width, template.Height)))
                    template.ConvertTo(destination, MatType.CV_64FC1);
                Cv2.Dft(spectrum.Value, spectrum.Value, DftFlags.None, template.Height);
                return spectrum;
            }
            catch { spectrum.Dispose(); throw; }
        }

        internal Lease AcquireLease(string cacheOutcome)
        {
            lock (_sync)
            {
                if (_released || !_cacheOwnerAlive)
                    throw new ObjectDisposedException(nameof(TemplateSpectraPair));
                var lease = new Lease(this, cacheOutcome);
                _leaseCount++;
                return lease;
            }
        }

        private Lease AcquireLocalLease(string cacheOutcome)
        {
            lock (_sync)
            {
                if (_released || !_cacheOwnerAlive)
                    throw new ObjectDisposedException(nameof(TemplateSpectraPair));
                var lease = new Lease(this, cacheOutcome);
                _cacheOwnerAlive = false;
                _leaseCount = 1;
                return lease;
            }
        }

        internal void ReleaseCacheOwner()
        {
            Padded? structure = null, visible = null;
            lock (_sync)
            {
                if (!_cacheOwnerAlive) return;
                _cacheOwnerAlive = false;
                if (_leaseCount == 0)
                    (structure, visible) = DetachStorage();
            }
            visible?.Dispose();
            structure?.Dispose();
        }

        internal void ReleaseLease()
        {
            Padded? structure = null, visible = null;
            lock (_sync)
            {
                if (_leaseCount <= 0) return;
                _leaseCount--;
                if (_leaseCount == 0 && !_cacheOwnerAlive)
                    (structure, visible) = DetachStorage();
            }
            visible?.Dispose();
            structure?.Dispose();
        }

        private (Padded? Structure, Padded? Visible) DetachStorage()
        {
            if (_released) return (null, null);
            _released = true;
            var structure = _structure;
            var visible = _visible;
            _structure = null;
            _visible = null;
            return (structure, visible);
        }

        internal sealed class Padded : IDisposable
        {
            private readonly Mat _storage;
            internal Mat Value { get; }

            internal Padded(Size size)
            {
                Mat? storage = null, value = null;
                try
                {
                    storage = new Mat(size.Height, checked(size.Width + 1),
                        MatType.CV_64FC1, Scalar.All(0));
                    value = new Mat(storage, new Rect(0, 0, size.Width, size.Height));
                    if (value.IsContinuous())
                        throw new InvalidOperationException("Unexpected prepared DFT layout");
                    _storage = storage; Value = value;
                }
                catch { value?.Dispose(); storage?.Dispose(); throw; }
            }

            public void Dispose() { Value.Dispose(); _storage.Dispose(); }
        }
    }
}
