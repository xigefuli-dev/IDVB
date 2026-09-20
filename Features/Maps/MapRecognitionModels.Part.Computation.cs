using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class CapturedGameFrame
{
    private IdvaNativeObservedExtractor.Result? _nativeObservedStructure;
    private Vpsg3LiveObservation? _vpsg3Observation;

    // The capture path can prepare this once; alignment/recovery borrow the same immutable observation.
    internal Vpsg3LiveObservation GetOrCreateVpsg3Observation()
    {
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _vpsg3Observation ??= Vpsg3FastLiveExtractor.Extract(Image, ViewportBounds);
        }
    }

    // One physical frame owns one immutable native observation across local,
    // global translation and scale recovery. Callers must not dispose it.
    internal IdvaNativeObservedExtractor.Result GetOrCreateNativeObservedStructure()
    {
        lock (_derivedFeaturesGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _nativeObservedStructure ??= IdvaNativeObservedExtractor.Process(Image);
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

