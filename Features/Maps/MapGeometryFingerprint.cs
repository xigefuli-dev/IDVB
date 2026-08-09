namespace IDVBuff.Features.Maps;

public sealed class MapGeometryFingerprint
{
    public MapRecord Map { get; init; } = new();
    public string FloorKey { get; init; } = "1f";
    public MapNormalizedPoint MainPoint { get; init; }
    public MapNormalizedPoint SidePoint { get; init; }
    public MapScreenRect MainReferenceBounds { get; init; }
    public MapScreenRect SideReferenceBounds { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public string RecognitionImagePath { get; init; } = string.Empty;
    public string OverlayImagePath { get; init; } = string.Empty;

    /// <summary>
    /// Actual gate icon width in the reference image, measured by template
    /// matching. Zero means "not measured" and the system falls back to
    /// <see cref="MainReferenceBounds"/> / <see cref="SideReferenceBounds"/>.
    /// </summary>
    public double ReferenceGateIconWidth { get; init; }
    /// <summary>Actual gate icon height in the reference image (see <see cref="ReferenceGateIconWidth"/>).</summary>
    public double ReferenceGateIconHeight { get; init; }
    /// <summary>True when both icon dimensions have been measured.</summary>
    public bool HasReferenceGateIconSize =>
        ReferenceGateIconWidth > 0d && ReferenceGateIconHeight > 0d;

    public double DeltaX => SidePoint.X - MainPoint.X;
    public double DeltaY => SidePoint.Y - MainPoint.Y;
    public double Distance => Math.Sqrt((DeltaX * DeltaX) + (DeltaY * DeltaY));
    public double Angle => Math.Atan2(DeltaY, DeltaX);
}

public sealed class MapGeometryCandidate
{
    public MapGeometryFingerprint Fingerprint { get; init; } = new();
    public GateDetection MainGate { get; init; } = new();
    public GateDetection SideGate { get; init; } = new();
    public MapNormalizedPoint ReferenceCenter { get; init; }
    public MapNormalizedPoint ScreenCenter { get; init; }
    public double EstimatedScaleX { get; init; }
    public double EstimatedScaleY { get; init; }
    public double VectorError { get; init; }
    public double DistanceError { get; init; }
    public double AngleError { get; init; }
    public double Score { get; init; }
    public double ConfirmationScore { get; set; }
}
