using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public sealed partial class CapturedGameFrame
{
    internal Mat? DiagnosticSourceImage { get; private set; }
    internal MapScreenRect DiagnosticSourceBounds { get; private set; }

    internal void RetainDiagnosticSource(CapturedGameFrame source)
    {
        if (!MapDiagnosticModeCapture.IsActive || DiagnosticSourceImage is not null)
            return;
        // Keep these same captured pixels alive across ROI crops; no second
        // capture or mutable capture surface participates in this snapshot.
        try
        {
            var image = source.DiagnosticSourceImage ?? source.Image;
            DiagnosticSourceImage = new Mat(image, new Rect(0, 0, image.Width, image.Height));
            DiagnosticSourceBounds = source.DiagnosticSourceImage is null
                ? source.ViewportBounds : source.DiagnosticSourceBounds;
        }
        catch { /* A diagnostic snapshot must not prevent recognition. */ }
    }
}
