using System;
using System.Collections.Generic;
using OpenCvSharp;
using IDVBuff.Pipeline;
using Padded = IDVBuff.Features.Maps.TemplateSpectrumCache.TemplateSpectraPair.Padded;

namespace IDVBuff.Features.Maps;

internal enum PreparedMatCorrelationRoute
{
    Prepared, UnsupportedVersion, UnsupportedTemplate, OwnerMismatch,
    TemplateMismatch, UnsupportedReference, IppEligible, MultiBlock,
    UnsupportedDftShape
}
// Borrowed templates must stay immutable/alive for one serial refinement loop.
// Only spectra/workspace belong to this object; no ROI or response is retained.
internal sealed class PreparedMatCorrelation : IDisposable
{
    private readonly object _sync = new();
    private readonly VisibleAwareCorrelationSession _owner;
    private readonly TemplateSpectrumCache? _cache;
    private readonly Mat _structure, _visible;
    private readonly Size _resizeTargetSize;
    private readonly Rect _bounds;
    private readonly int _visibleMaskErodePixels, _factor;
    private readonly VisibleAwareCorrelationMode _requestedBackend;
    private readonly string _actualBackend;
    private readonly Size _templateSize;
    private readonly Dictionary<Size, Pool> _pools = new();
    private double? _structureSum;
    private bool _disposed;

    private PreparedMatCorrelation(
        VisibleAwareCorrelationSession owner,
        TemplateSpectrumCache? cache,
        QueryGeometry query,
        Mat structure,
        Mat visible,
        int factor,
        int visibleMaskErodePixels,
        VisibleAwareCorrelationMode requestedBackend,
        string actualBackend)
    {
        _owner = owner; _cache = cache;
        _structure = structure; _visible = visible;
        _resizeTargetSize = query.ResizeTargetSize;
        _bounds = query.Bounds;
        _visibleMaskErodePixels = visibleMaskErodePixels;
        _factor = factor;
        _requestedBackend = requestedBackend;
        _actualBackend = actualBackend;
        _templateSize = structure.Size();
    }

    // Only the marked, frame-owned native computation feature carries a cache.
    // Other provenance keeps the existing call-local prepared spectra path.
    internal static PreparedMatCorrelation? TryCreate(
        VisibleAwareCorrelationSession owner,
        QueryGeometry query,
        Mat structure,
        Mat visible,
        int factor,
        int visibleMaskErodePixels,
        VisibleAwareCorrelationMode requestedBackend,
        string actualBackend,
        out PreparedMatCorrelationRoute route)
    {
        route = PreparedMatCorrelationRoute.UnsupportedVersion;
        if (Cv2.GetVersionString() != "4.13.0") return null;
        route = PreparedMatCorrelationRoute.UnsupportedTemplate;
        var source = query.TemplateSpectraSource;
        if (factor <= 1
            || requestedBackend != VisibleAwareCorrelationMode.CoarseMat
            || actualBackend != "Mat"
            || owner.RequestedMode != requestedBackend
            || owner.ActualBackend != actualBackend
            || query.Bounds.Width < 1 || query.Bounds.Height < 1
            || structure.Empty() || visible.Empty()
            || structure.Type() != MatType.CV_32FC1
            || visible.Type() != MatType.CV_32FC1
            || structure.Size() != visible.Size()
            || structure.Size() != new Size(query.Bounds.Width, query.Bounds.Height)
            || structure.Width < 2 || structure.Height < 2) return null;
        TemplateSpectrumCache? cache = null;
        if (source is not null
            && source.IsFrameOwnedNativeComputationFeature
            && query.ResizeTargetSize.Width > 0
            && query.ResizeTargetSize.Height > 0)
            cache = source.GetOrCreateTemplateSpectrumCache();
        route = PreparedMatCorrelationRoute.Prepared;
        return new PreparedMatCorrelation(
            owner,
            cache,
            query,
            structure,
            visible,
            factor,
            Math.Clamp(visibleMaskErodePixels, 0, 3),
            requestedBackend,
            actualBackend);
    }

    // false means unsupported, BEFORE allocations/transforms: call stock backend.
    // An exception during supported Mat execution propagates; never retry it.
    // The caller/session must validate the returned response exactly as before.
    internal bool TryCorrelate(object owner, Mat reference,
        Mat structure, Mat visible, out Mat? response,
        out PreparedMatCorrelationRoute route,
        out string cacheDiagnostic)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            response = null;
            cacheDiagnostic = "not-used";
            route = PreparedMatCorrelationRoute.OwnerMismatch;
            if (!ReferenceEquals(owner, _owner)) return false;
            route = PreparedMatCorrelationRoute.TemplateMismatch;
            if (!ReferenceEquals(structure, _structure)
                || !ReferenceEquals(visible, _visible)
                || structure.Size() != _templateSize || visible.Size() != _templateSize
                || structure.Type() != MatType.CV_32FC1
                || visible.Type() != MatType.CV_32FC1) return false;
            route = PreparedMatCorrelationRoute.UnsupportedReference;
            if (reference.Empty() || reference.Type() != MatType.CV_32FC1) return false;
            if (!TrySingleBlock(reference.Size(), _templateSize,
                out var responseSize, out var dftSize, out route)) return false;

            if (!_pools.TryGetValue(dftSize, out var pool))
            {
                // CreateQuery's explicit target fixes all template resizes; the
                // bounds and erosion key capture the remaining template work.
                var key = new TemplateSpectrumCacheKey(
                    _resizeTargetSize,
                    _bounds.X,
                    _bounds.Y,
                    _bounds.Width,
                    _bounds.Height,
                    _visibleMaskErodePixels,
                    _factor,
                    _requestedBackend,
                    _actualBackend,
                    dftSize,
                    _templateSize);
                TemplateSpectrumCacheStatus cacheStatus;
                TemplateSpectrumCache.Lease? spectra = null;
                using var spectraSpan = MapOperationTraceAmbient.StartChild(
                    "prepared_template_spectra",
                    MapOperationWaitKind.Compute,
                    route: $"dft={dftSize.Width}x{dftSize.Height}");
                if (_cache is null)
                {
                    cacheStatus = new TemplateSpectrumCacheStatus(
                        "local-only", 0, 0);
                    spectra = TemplateSpectrumCache.TemplateSpectraPair
                        .CreateLocalLease(dftSize, structure, visible,
                            cacheStatus.Outcome);
                }
                else if (!_cache.TryAcquire(
                    key,
                    structure,
                    visible,
                    out spectra,
                    out cacheStatus))
                {
                    spectra = TemplateSpectrumCache.TemplateSpectraPair
                        .CreateLocalLease(dftSize, structure, visible,
                            cacheStatus.Outcome);
                    cacheStatus = cacheStatus with
                    {
                        Outcome = $"local/{cacheStatus.Outcome}"
                    };
                }

                try
                {
                    cacheDiagnostic = cacheStatus.ToDiagnostic(dftSize);
                    spectraSpan.Complete(terminalReason: cacheDiagnostic);
                    var created = Pool.Create(dftSize, spectra!, cacheDiagnostic);
                    // The pool owns the lease from here, including failed
                    // dictionary publication. Diagnostics own no resources.
                    spectra = null;
                    try { _pools.Add(dftSize, created); }
                    catch { created.Dispose(); throw; }
                    pool = created;
                }
                finally
                {
                    spectra?.Dispose();
                }
            }
            else
            {
                cacheDiagnostic = $"session-reuse/{pool.CacheDiagnostic}";
            }

            pool.Image.Value.SetTo(Scalar.All(0));
            using (var destination = new Mat(pool.Image.Value,
                new Rect(0, 0, reference.Width, reference.Height)))
                reference.ConvertTo(destination, MatType.CV_64FC1);
            Cv2.Dft(pool.Image.Value, pool.Image.Value, DftFlags.None, reference.Height);
            pool.Image.Value.CopyTo(pool.Work.Value);

            using var tp = Finish(pool.Image.Value,
                pool.Spectra.StructureSpectrum, responseSize);
            using var refVisible = Finish(pool.Work.Value,
                pool.Spectra.VisibleSpectrum, responseSize);
            using var union = new Mat();
            _structureSum ??= Cv2.Sum(structure).Val0;
            Cv2.Add(refVisible, _structureSum.Value, union);
            Cv2.Subtract(union, tp, union);
            Cv2.Max(union, 1d, union);
            var result = new Mat();
            try
            {
                Cv2.Divide(tp, union, result);
                Cv2.Min(result, 1d, result);
                Cv2.Max(result, 0d, result);
                response = result;
            }
            catch { result.Dispose(); throw; }
            route = PreparedMatCorrelationRoute.Prepared;
            return true;
        }
    }

    private static bool TrySingleBlock(Size reference, Size template,
        out Size response, out Size dft, out PreparedMatCorrelationRoute route)
    {
        response = default; dft = default;
        route = PreparedMatCorrelationRoute.UnsupportedReference;
        if (reference.Width < template.Width || reference.Height < template.Height)
            return false;
        response = new Size(reference.Width - template.Width + 1,
            reference.Height - template.Height + 1);
        // OpenCV 4.13.0 ipp_matchTemplate permits template area <= image area/4.
        // Exclude this route even if IPP happens to be unavailable on this host.
        var templateArea = (long)template.Width * template.Height;
        route = PreparedMatCorrelationRoute.UnsupportedDftShape;
        if (templateArea > long.MaxValue / 4
            || template.Width > int.MaxValue / 4.5
            || template.Height > int.MaxValue / 4.5) return false;
        route = PreparedMatCorrelationRoute.IppEligible;
        if (templateArea * 4 <= (long)reference.Width * reference.Height) return false;

        // cv::crossCorr, templmatch.cpp: blockScale=4.5, minBlockSize=256.
        // Preserve the exact transform shape for each clipped ROI; no padding
        // to a shared larger transform and no multi-block approximation.
        var blockWidth = Math.Min(Math.Max(
            (int)Math.Round(template.Width * 4.5, MidpointRounding.ToEven),
            256 - template.Width + 1), response.Width);
        var blockHeight = Math.Min(Math.Max(
            (int)Math.Round(template.Height * 4.5, MidpointRounding.ToEven),
            256 - template.Height + 1), response.Height);
        route = PreparedMatCorrelationRoute.UnsupportedDftShape;
        var paddedWidth = (long)blockWidth + template.Width - 1;
        var paddedHeight = (long)blockHeight + template.Height - 1;
        if (paddedWidth > int.MaxValue || paddedHeight > int.MaxValue) return false;
        var dftWidth = Cv2.GetOptimalDFTSize((int)paddedWidth);
        var dftHeight = Cv2.GetOptimalDFTSize((int)paddedHeight);
        if (dftWidth < 1 || dftHeight < 2 || dftWidth == int.MaxValue) return false;
        dft = new Size(Math.Max(dftWidth, 2), dftHeight);
        blockWidth = Math.Min(dft.Width - template.Width + 1, response.Width);
        blockHeight = Math.Min(dft.Height - template.Height + 1, response.Height);
        route = PreparedMatCorrelationRoute.MultiBlock;
        if (blockWidth != response.Width || blockHeight != response.Height) return false;
        route = PreparedMatCorrelationRoute.UnsupportedDftShape;
        if (dft.Width < reference.Width || dft.Height < reference.Height) return false;
        return true;
    }

    private static Mat Finish(Mat imageSpectrum, Mat templateSpectrum, Size responseSize)
    {
        Cv2.MulSpectrums(imageSpectrum, templateSpectrum, imageSpectrum,
            DftFlags.None, true);
        Cv2.Dft(imageSpectrum, imageSpectrum,
            DftFlags.Inverse | DftFlags.Scale, responseSize.Height);
        using var small = new Mat(imageSpectrum,
            new Rect(0, 0, responseSize.Width, responseSize.Height));
        var response = new Mat();
        try { small.ConvertTo(response, MatType.CV_32FC1); return response; }
        catch { response.Dispose(); throw; }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pool in _pools.Values) pool.Dispose();
            _pools.Clear();
        }
    }

    private sealed class Pool : IDisposable
    {
        internal TemplateSpectrumCache.Lease Spectra { get; }
        internal Padded Image { get; }
        internal Padded Work { get; }
        internal string CacheDiagnostic { get; }

        private Pool(
            TemplateSpectrumCache.Lease spectra,
            Padded image,
            Padded work,
            string cacheDiagnostic)
        {
            Spectra = spectra;
            Image = image;
            Work = work;
            CacheDiagnostic = cacheDiagnostic;
        }

        internal static Pool Create(
            Size size,
            TemplateSpectrumCache.Lease spectra,
            string cacheDiagnostic)
        {
            Padded? image = null, work = null;
            try
            {
                image = new Padded(size); work = new Padded(size);
                return new Pool(spectra, image, work, cacheDiagnostic);
            }
            catch
            {
                work?.Dispose(); image?.Dispose();
                spectra.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Work.Dispose();
            Image.Dispose();
            Spectra.Dispose();
        }
    }
}
