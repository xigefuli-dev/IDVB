using OpenCvSharp;

namespace IDVBuff.Features.Maps;

public enum MapFrontEntryFamilyStatus
{
    Feasible,
    CertifiedInfeasible,
    NumericallyUnresolved
}

public enum MapFrontEntryFamilyTermination
{
    Completed,
    FixedScale,
    InvalidScaleInterval,
    NoPositiveScale,
    NonFiniteInput,
    NonFiniteArithmetic,
    ContactOrInsufficientLowerBound,
    Cancelled,
    BudgetExpired
}

/// <summary>
/// A conservative outer bracket. Its endpoints and the scales between them
/// are not individually verified models. Zero can be retained as an outer
/// limit; every actual model still requires a strictly positive scale.
/// </summary>
public readonly record struct MapFrontEntryScaleBracket(double Minimum, double Maximum);

/// <summary>
/// One original correspondence. At scale s the ideal translation disk has
/// center Query - s*Source and physical radius3. Downstream numerical bounds
/// must be conservative and actual models must recheck the original residual.
/// The three disks are constraints, not a computed translation intersection.
/// </summary>
public readonly record struct MapFrontEntryTranslationDisk(
    Point2d Source, Point2d Query, double PhysicalRadius);

public sealed record MapFrontEntryScaleSample
{
    public double Scale { get; init; }
    public Point2d CenteredTranslation { get; init; }
    public Point2d Translation { get; init; }
    public double MecRadius { get; init; }
    public double? RadiusLowerBound { get; init; }
    public double? RadiusUpperBound { get; init; }
    public IReadOnlyList<double> OriginalPhysicalResiduals { get; init; } = Array.Empty<double>();
    public bool OriginalPhysical3Verified { get; init; }
    public bool ArithmeticFinite { get; init; }
}

/// <summary>
/// This result covers only the supplied three-correspondence scale interval.
/// Feasible means one original-coordinate model passed Hypot<=3, with no
/// acceptance epsilon. It does not close other scales, translations, roles,
/// pixels, poses or map identities. CertifiedInfeasible requires a conservative
/// lower bound strictly above3. Otherwise the result remains unresolved.
/// </summary>
public sealed record MapFrontEntryFamilyResult
{
    public MapFrontEntryFamilyStatus Status { get; init; }
    public MapFrontEntryFamilyTermination Termination { get; init; }
    public MapFrontEntryScaleBracket RequestedScaleBracket { get; init; }
    public MapFrontEntryScaleBracket? PossibleScaleBracket { get; init; }
    public MapFrontEntryScaleBracket? MinimumSearchBracket { get; init; }
    public MapFrontEntryScaleSample? Representative { get; init; }
    public MapFrontEntryScaleSample? BestSample { get; init; }
    public double? ConservativeMinimumRadiusLowerBound { get; init; }
    public double? OriginalArithmeticAllowance { get; init; }
    public bool LowerOuterEndpointCertifiedOutside { get; init; }
    public bool UpperOuterEndpointCertifiedOutside { get; init; }
    public bool BoundaryResolutionLimited { get; init; }
    public bool RepresentativeTouchesPhysicalBoundary { get; init; }
    public bool ZeroWidthInterval { get; init; }
    public int Evaluations { get; init; }
    public IReadOnlyList<MapFrontEntryTranslationDisk> OriginalDisks { get; init; } =
        Array.Empty<MapFrontEntryTranslationDisk>();
}
