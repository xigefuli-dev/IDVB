using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class CapturedGameFrame
{
    private IdvaNativeObservedExtractor.Result? _nativeObservedStructure;
    private Vpsg3LiveObservation? _vpsg3Observation;
    private IdvaNativeObservedExtractor.Result? _nativePrebuiltLiveSource;
    private Size _nativePrebuiltLiveObservedEdgesSize;
    private MatType _nativePrebuiltLiveObservedEdgesType;
    private Size _nativePrebuiltLiveValidMaskSize;
    private MatType _nativePrebuiltLiveValidMaskType;
    private Size _nativePrebuiltLiveComputationSize;
    private MapStructureFeatures? _nativePrebuiltLiveComputation;
    private MapStructureFeatures? _nativePrebuiltLiveOriginal;
    private double _nativePrebuiltLiveFirstExtractionMilliseconds;

    // The frame owns both immutable features. Callers borrow them and must not dispose or mutate them.
    internal bool TryGetOrCreateNativePrebuiltLiveStructureFeatures(
        System.Diagnostics.Stopwatch extractionTimer,
        out MapStructureFeatures computation,
        out MapStructureFeatures original,
        out bool cacheHit,
        out double originalExtractionMilliseconds)
    {
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var source = GetOrCreateNativeObservedStructure();
            var observedEdges = source.ObservedEdges;
            var validMask = source.ValidMask;
            var computationSize = ComputationImage.Size();

            if (_nativePrebuiltLiveComputation is { } existingComputation
                && _nativePrebuiltLiveOriginal is { } existingOriginal)
            {
                if (!ReferenceEquals(_nativePrebuiltLiveSource, source)
                    || _nativePrebuiltLiveObservedEdgesSize.Width != observedEdges.Width
                    || _nativePrebuiltLiveObservedEdgesSize.Height != observedEdges.Height
                    || !_nativePrebuiltLiveObservedEdgesType.Equals(observedEdges.Type())
                    || _nativePrebuiltLiveValidMaskSize.Width != validMask.Width
                    || _nativePrebuiltLiveValidMaskSize.Height != validMask.Height
                    || !_nativePrebuiltLiveValidMaskType.Equals(validMask.Type())
                    || _nativePrebuiltLiveComputationSize.Width != computationSize.Width
                    || _nativePrebuiltLiveComputationSize.Height != computationSize.Height)
                {
                    computation = null!;
                    original = null!;
                    cacheHit = false;
                    originalExtractionMilliseconds = 0d;
                    return false;
                }

                computation = existingComputation;
                original = existingOriginal;
                cacheHit = true;
                originalExtractionMilliseconds =
                    _nativePrebuiltLiveFirstExtractionMilliseconds;
                return true;
            }

            MapStructureFeatures? createdComputation = null;
            MapStructureFeatures? createdOriginal = null;
            try
            {
                using (var computationEdges = new Mat())
                using (var computationMask = new Mat())
                {
                    Cv2.Resize(
                        observedEdges,
                        computationEdges,
                        computationSize,
                        interpolation: InterpolationFlags.Nearest);
                    Cv2.Resize(
                        validMask,
                        computationMask,
                        computationSize,
                        interpolation: InterpolationFlags.Nearest);

                    createdComputation = MapStructurePreprocessor.UseNativeObservedStructureLine(
                        computationEdges,
                        computationMask);
                    createdComputation.MarkFrameOwnedNativeComputationFeature();
                    createdOriginal = MapStructurePreprocessor.UseNativeObservedStructureLine(
                        observedEdges,
                        validMask);
                    extractionTimer.Stop();
                }

                _nativePrebuiltLiveSource = source;
                _nativePrebuiltLiveObservedEdgesSize = observedEdges.Size();
                _nativePrebuiltLiveObservedEdgesType = observedEdges.Type();
                _nativePrebuiltLiveValidMaskSize = validMask.Size();
                _nativePrebuiltLiveValidMaskType = validMask.Type();
                _nativePrebuiltLiveComputationSize = computationSize;
                _nativePrebuiltLiveFirstExtractionMilliseconds =
                    extractionTimer.Elapsed.TotalMilliseconds;
                _nativePrebuiltLiveComputation = createdComputation;
                _nativePrebuiltLiveOriginal = createdOriginal;

                computation = createdComputation;
                original = createdOriginal;
                cacheHit = false;
                originalExtractionMilliseconds =
                    _nativePrebuiltLiveFirstExtractionMilliseconds;
                return true;
            }
            catch
            {
                createdComputation?.Dispose();
                createdOriginal?.Dispose();
                throw;
            }
        }
    }

    // The capture path can prepare this once; alignment/recovery borrow the same immutable observation.
    internal Vpsg3LiveObservation GetOrCreateVpsg3Observation()
    {
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vpsg3Observation ??= Vpsg3FastLiveExtractor.Extract(Image, ViewportBounds,
                excludedScreenRegions: UiExclusionRegions);
        }
    }

    // One physical frame owns one immutable native observation across local,
    // global translation and scale recovery. Callers must not dispose it.
    internal IdvaNativeObservedExtractor.Result GetOrCreateNativeObservedStructure()
    {
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _nativeObservedStructure ??= IdvaNativeObservedExtractor.Process(Image,
                ViewportBounds, UiExclusionRegions);
        }
    }

    internal const int ComputationViewportWidth = 1003;
    private Mat? _ownedComputationImage;

    /// <summary>Search input capped at the observed 1080P viewport density.</summary>
    public Mat ComputationImage
    {
        get
        {
            lock (_derivedFeaturesGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_ownedComputationImage is not null
                    || Image.Width <= ComputationViewportWidth)
                {
                    return _ownedComputationImage ?? Image;
                }

                var height = Math.Max(1, (int)Math.Round(
                    Image.Height
                    * (ComputationViewportWidth / (double)Image.Width)));
                _ownedComputationImage = new Mat();
                Cv2.Resize(Image, _ownedComputationImage,
                    new Size(ComputationViewportWidth, height), 0d, 0d,
                    InterpolationFlags.Area);
                return _ownedComputationImage;
            }
        }
    }

    internal bool HasCreatedComputationImage
    {
        get
        {
            lock (_derivedFeaturesGate)
                return _ownedComputationImage is not null;
        }
    }

    /// <summary>Physical pixels represented by one computation-image pixel.</summary>
    public double PhysicalPixelsPerComputationPixel =>
        Image.Width / (double)ComputationImage.Width;

    internal Rect ToComputationRect(Rect value)
    {
        var ratio = PhysicalPixelsPerComputationPixel;
        return new Rect(
            (int)Math.Round(value.X / ratio),
            (int)Math.Round(value.Y / ratio),
            Math.Max(1, (int)Math.Round(value.Width / ratio)),
            Math.Max(1, (int)Math.Round(value.Height / ratio)));
    }
}

