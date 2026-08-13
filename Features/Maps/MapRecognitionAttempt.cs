namespace IDVBuff.Features.Maps;

public sealed class RuntimeMapRecognition
{
    public MapRecord Map { get; init; } = new();
    public MapRecognitionResult Result { get; init; } = new();
    public string FloorImagePath { get; init; } = string.Empty;
}

public sealed class MapRecognitionChoice
{
    public RuntimeMapRecognition Recognition { get; init; } = new();
    public double VectorError { get; init; }
    /// <summary>
    /// Evidence score used only for ordering the chooser. For reference-only
    /// side-entrance clues this is template similarity, not confidence.
    /// </summary>
    public double EvidenceScore { get; init; } = double.NaN;
    public bool IsReferenceOnly { get; init; }
    public string EvidenceLabel { get; init; } = string.Empty;
    /// <summary>
    /// Optional evidence-specific ordering decided before the chooser is
    /// opened. Existing recognition routes leave this at its default and keep
    /// their confidence ordering; side-entrance verification uses it to retain
    /// the strict geometry tie-break order.
    /// </summary>
    public int PreferredOrder { get; init; } = int.MaxValue;
    public double RawConfidence => double.IsFinite(EvidenceScore)
        ? EvidenceScore
        : Recognition.Result.Confidence;
}

public sealed class MapRecognitionAttempt
{
    private RuntimeMapRecognition? _recognition;
    private MapScanDiagnostics _diagnostics = new();

    public RuntimeMapRecognition? Recognition
    {
        get => _recognition;
        init
        {
            _recognition = value;
            PopulateConfidenceDiagnostics();
        }
    }

    public IReadOnlyList<MapRecognitionChoice> Choices { get; init; } = [];

    public MapScanDiagnostics Diagnostics
    {
        get => _diagnostics;
        init
        {
            _diagnostics = value ?? new MapScanDiagnostics();
            PopulateConfidenceDiagnostics();
        }
    }

    public string FailureReason { get; init; } = string.Empty;
    public MapStructureRegistrationResult? StructureResult { get; init; }
    public GateDetectionResult? GateDetectionResult { get; init; }
    public bool StructureAttempted { get; init; }
    public bool StructureAccepted { get; init; }
    public string StructureFailureReason { get; init; } = string.Empty;
    public AlignmentSearchStage SearchStage { get; init; }

    private void PopulateConfidenceDiagnostics()
    {
        if (_recognition is null)
            return;
        _diagnostics.IdentityConfidence =
            _recognition.Result.IdentityConfidence;
        _diagnostics.LocalizationConfidence =
            _recognition.Result.LocalizationConfidence;
    }
}

/// <summary>
/// Pure side-entrance evidence rules shared by orchestration, logging, and
/// source-linked tests. Template evidence proposes an identity; successful
/// strict structure validation is the only automatic promotion gate.
/// </summary>
internal static class SideEntranceCandidateEvidence
{
    private const double StrictInitialIdentityChamferLimit = 3.0d;

    public static bool ApplyStructureAttempt(
        SideEntranceScanCandidate candidate,
        MapRecognitionAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(attempt);

        var structure = attempt.StructureResult;
        var breakdown = structure?.ConfidenceBreakdown;
        var best = structure?.Candidates
            .Where(item => double.IsFinite(item.CompositeCost))
            .OrderBy(item => item.CompositeCost)
            .FirstOrDefault();
        candidate.StructureScore = structure?.Confidence ?? 0d;
        candidate.IdentityConfidence =
            attempt.Recognition?.Result.IdentityConfidence ?? 0d;
        candidate.RawChamferPixels = ResolveRawChamferPixels(structure);
        candidate.StructureCompositeCost = structure?.BestScore
            ?? double.PositiveInfinity;
        candidate.StructureEdgeCoverage = breakdown?.EdgeCoverage
            ?? best?.EdgeCoverage
            ?? 0d;
        candidate.StructureOccupancyCoverage = breakdown?.OccupancyCoverage
            ?? best?.OccupancyCoverage
            ?? 0d;
        candidate.StructureCandidateMargin = structure?.CandidateMargin ?? 0d;

        var rawChamferAccepted = double.IsFinite(candidate.RawChamferPixels)
            && candidate.RawChamferPixels <= StrictInitialIdentityChamferLimit;
        if (attempt.StructureAccepted
            && attempt.Recognition is not null
            && rawChamferAccepted)
        {
            candidate.Disposition = SideEntranceCandidateDisposition.Reliable;
            candidate.RejectionReason = SideEntranceRejectionReason.None;
            candidate.RejectionDetail = string.Empty;
            return true;
        }

        candidate.Disposition = SideEntranceCandidateDisposition.NeedsVerification;
        candidate.RejectionReason = SideEntranceRejectionReason.StructureRejected;
        if (!rawChamferAccepted)
        {
            candidate.RejectionDetail =
                $"结构 Chamfer {candidate.RawChamferPixels:F2}px 超过 "
                + $"{StrictInitialIdentityChamferLimit:F1}px 严格上限。";
            return false;
        }
        candidate.RejectionDetail = string.IsNullOrWhiteSpace(
            attempt.StructureFailureReason)
                ? attempt.FailureReason
                : attempt.StructureFailureReason;
        if (string.IsNullOrWhiteSpace(candidate.RejectionDetail))
        {
            candidate.RejectionDetail = attempt.StructureAccepted
                ? "结构验证未产生可提交的定位结果。"
                : "未通过严格结构验证。";
        }
        return false;
    }

    public static IOrderedEnumerable<T> OrderVerified<T>(
        IEnumerable<T> source,
        Func<T, SideEntranceScanCandidate> candidateSelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(candidateSelector);
        return source
            .OrderBy(item => FiniteOrInfinity(
                candidateSelector(item).RawChamferPixels))
            .ThenByDescending(item => FiniteOrZero(
                candidateSelector(item).StructureEdgeCoverage))
            .ThenByDescending(item => FiniteOrZero(
                candidateSelector(item).StructureOccupancyCoverage))
            .ThenByDescending(item => FiniteOrZero(
                candidateSelector(item).StructureCandidateMargin))
            .ThenByDescending(item => FiniteOrZero(
                candidateSelector(item).MatchScore));
    }

    public static IReadOnlyList<SideEntranceScanCandidate>
        SelectVerificationCandidates(
            IEnumerable<SideEntranceScanCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(candidate => candidate.MatchScore >=
                SideEntranceScanRules.MinimumVerificationSimilarity)
            .ToArray();
    }

    public static double ResolveRawChamferPixels(
        MapStructureRegistrationResult? structure)
    {
        if (structure?.ConfidenceBreakdown is { } breakdown
            && double.IsFinite(breakdown.ChamferPixels))
        {
            return breakdown.ChamferPixels;
        }

        if (structure is null)
            return double.PositiveInfinity;

        return structure.Candidates
            .Where(item => double.IsFinite(item.ChamferPixels))
            .OrderBy(item => FiniteOrInfinity(item.CompositeCost))
            .Select(item => item.ChamferPixels)
            .FirstOrDefault(double.PositiveInfinity);
    }

    public static Dictionary<string, object?> BuildStructureMetricLogDetails(
        MapStructureRegistrationResult? structure,
        double effectiveChamferLimit)
    {
        return new Dictionary<string, object?>
        {
            ["rawChamferPixels"] = structure is null
                ? null
                : FiniteOrNull(ResolveRawChamferPixels(structure)),
            ["compositeCost"] = structure is null
                ? null
                : FiniteOrNull(structure.BestScore),
            ["usedRestrictedSearch"] = structure?.UsedRestrictedSearch,
            ["effectiveChamferLimit"] = effectiveChamferLimit
        };
    }

    private static double FiniteOrInfinity(double value) =>
        double.IsFinite(value) ? value : double.PositiveInfinity;

    private static double FiniteOrZero(double value) =>
        double.IsFinite(value) ? value : 0d;

    private static object? FiniteOrNull(double value) =>
        double.IsFinite(value) ? value : null;
}
