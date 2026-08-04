using System.Text.Json;
using System.Text.Json.Serialization;

namespace IDVBuff.Features.Maps;

public readonly record struct MapReferencePoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct MapViewportPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct MapScreenPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public readonly record struct MapViewportOrigin(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
    public MapReferencePoint AsPoint() => new(X, Y);
}

public sealed class MapReferenceBounds
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1d;
    public double Height { get; set; } = 1d;

    [JsonIgnore]
    public double Right => X + Width;

    [JsonIgnore]
    public double Bottom => Y + Height;

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(X)
        && double.IsFinite(Y)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0d
        && Height > 0d;

    public MapReferenceBounds Clone() => new()
    {
        X = X,
        Y = Y,
        Width = Width,
        Height = Height
    };

    public static MapReferenceBounds FullImage(int width, int height) => new()
    {
        Width = Math.Max(1, width),
        Height = Math.Max(1, height)
    };

    public bool Contains(MapReferencePoint point, double tolerance = 0d) =>
        point.IsFinite
        && point.X >= X - tolerance
        && point.Y >= Y - tolerance
        && point.X <= Right + tolerance
        && point.Y <= Bottom + tolerance;

    public MapReferencePoint Clamp(MapReferencePoint point) => new(
        Math.Clamp(point.X, X, Right),
        Math.Clamp(point.Y, Y, Bottom));

    public MapViewportOrigin ClampViewportOrigin(
        MapViewportOrigin origin,
        double viewportWidth,
        double viewportHeight)
    {
        if (!IsValid
            || !origin.IsFinite
            || !double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0d
            || viewportHeight <= 0d)
        {
            return new MapViewportOrigin(X, Y);
        }

        // A native map canvas can be larger than the projected reference map.
        // In that case the valid origin interval is reversed: the reference
        // may sit anywhere between the canvas's left/top and right/bottom
        // edges while remaining fully visible.
        var minimumX = Math.Min(X, Right - viewportWidth);
        var maximumX = Math.Max(X, Right - viewportWidth);
        var minimumY = Math.Min(Y, Bottom - viewportHeight);
        var maximumY = Math.Max(Y, Bottom - viewportHeight);
        return new MapViewportOrigin(
            Math.Clamp(origin.X, minimumX, maximumX),
            Math.Clamp(origin.Y, minimumY, maximumY));
    }
}

/// <summary>
/// Maps full-reference pixels to physical screen pixels. Runtime alignment
/// always uses one uniform scale and one fixed rotation.
/// </summary>
public sealed class MapSimilarityTransform
{
    public double Scale { get; init; } = 1d;
    public double RotationDegrees { get; init; }
    public double TranslationX { get; init; }
    public double TranslationY { get; init; }

    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Scale)
        && Scale > 0d
        && double.IsFinite(RotationDegrees)
        && double.IsFinite(TranslationX)
        && double.IsFinite(TranslationY);

    public MapScreenPoint ToScreen(MapReferencePoint point)
    {
        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new MapScreenPoint(
            ((point.X * cosine) - (point.Y * sine)) * Scale + TranslationX,
            ((point.X * sine) + (point.Y * cosine)) * Scale + TranslationY);
    }

    public MapReferencePoint ToReference(MapScreenPoint point)
    {
        if (!IsValid)
            return new MapReferencePoint(double.NaN, double.NaN);
        var scaledX = (point.X - TranslationX) / Scale;
        var scaledY = (point.Y - TranslationY) / Scale;
        var radians = -RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        return new MapReferencePoint(
            (scaledX * cosine) - (scaledY * sine),
            (scaledX * sine) + (scaledY * cosine));
    }

    public MapOverlayTransform ToOverlayTransform(
        int referenceWidth,
        int referenceHeight,
        double residualPixels = 0d) =>
        new()
        {
            ScaleX = Scale,
            ScaleY = Scale,
            OffsetX = TranslationX,
            OffsetY = TranslationY,
            ReferenceCenterX = referenceWidth / 2d,
            ReferenceCenterY = referenceHeight / 2d,
            ScreenCenterX = ToScreen(
                new MapReferencePoint(
                    referenceWidth / 2d,
                    referenceHeight / 2d)).X,
            ScreenCenterY = ToScreen(
                new MapReferencePoint(
                    referenceWidth / 2d,
                    referenceHeight / 2d)).Y,
            ReferenceWidth = referenceWidth,
            ReferenceHeight = referenceHeight,
            OrientationDegrees = NormalizeRotation(RotationDegrees),
            AlignmentMode = MapOverlayAlignmentMode.Uniform,
            MaximumResidualPixels = Math.Max(0d, residualPixels)
        };

    public static MapSimilarityTransform FromOverlay(
        MapOverlayTransform transform) =>
        new()
        {
            Scale = (transform.ScaleX + transform.ScaleY) / 2d,
            RotationDegrees = transform.OrientationDegrees,
            TranslationX = transform.OffsetX,
            TranslationY = transform.OffsetY
        };

    private static int NormalizeRotation(double degrees)
    {
        var normalized = ((int)Math.Round(degrees) % 360 + 360) % 360;
        return normalized;
    }
}

public enum MapSessionState
{
    Closed,
    OpeningDetected,
    WaitingForStableFrames,
    IdentifyingMap,
    CoarseLocating,
    FineLocating,
    Confirming,
    Locked,
    LowConfidence,
    Lost,
    RecalibrationRequired
}

public enum MapRecalibrationReason
{
    None,
    MapReopened,
    WindowChanged,
    ResolutionChanged,
    DpiChanged,
    ViewportChanged,
    NativeScaleChanged,
    NativeRotationChanged,
    BackgroundMismatch,
    TransformError,
    MapIdentityChanged,
    FloorChanged,
    AlignmentLost
}

public enum MapLocationMethod
{
    None,
    DualAnchor,
    SingleAnchor,
    AuxiliaryAnchor,
    StructureTranslation,
    Manual
}

public sealed class MapWindowSignature : IEquatable<MapWindowSignature>
{
    public long WindowHandle { get; init; }
    public int ClientX { get; init; }
    public int ClientY { get; init; }
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int ViewportX { get; init; }
    public int ViewportY { get; init; }
    public int ViewportWidth { get; init; }
    public int ViewportHeight { get; init; }
    public uint Dpi { get; init; } = 96;

    public bool Equals(MapWindowSignature? other) => other is not null
        && WindowHandle == other.WindowHandle
        && ClientX == other.ClientX
        && ClientY == other.ClientY
        && ClientWidth == other.ClientWidth
        && ClientHeight == other.ClientHeight
        && ViewportX == other.ViewportX
        && ViewportY == other.ViewportY
        && ViewportWidth == other.ViewportWidth
        && ViewportHeight == other.ViewportHeight
        && Dpi == other.Dpi;

    public override bool Equals(object? obj) => Equals(obj as MapWindowSignature);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(WindowHandle);
        hash.Add(ClientX);
        hash.Add(ClientY);
        hash.Add(ClientWidth);
        hash.Add(ClientHeight);
        hash.Add(ViewportX);
        hash.Add(ViewportY);
        hash.Add(ViewportWidth);
        hash.Add(ViewportHeight);
        hash.Add(Dpi);
        return hash.ToHashCode();
    }
}

/// <summary>V6: reads Floor from JSON as either int (1→"1f", 2→"2f") or string.</summary>
internal sealed class FloorKeyJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32() switch
            {
                1 => "1f",
                2 => "2f",
                _ => reader.GetInt32().ToString()
            },
            JsonTokenType.String => reader.GetString(),
            _ => null
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

public sealed class MapAlignmentCalibration
{
    public Guid MapId { get; set; }

    [JsonConverter(typeof(FloorKeyJsonConverter))]
    public string Floor { get; set; } = "1f";
    public DateTimeOffset MapUpdatedAt { get; set; }
    public int ReferenceWidth { get; set; }
    public int ReferenceHeight { get; set; }
    public double UniformScale { get; set; }
    public double RotationDegrees { get; set; }
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
    public uint Dpi { get; set; } = 96;
    public double Confidence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        MapId != Guid.Empty
        && ReferenceWidth > 0
        && ReferenceHeight > 0
        && double.IsFinite(UniformScale)
        && UniformScale > 0d
        && double.IsFinite(RotationDegrees)
        && ClientWidth > 0
        && ClientHeight > 0
        && ViewportWidth > 0
        && ViewportHeight > 0
        && Dpi > 0;

    public bool Matches(
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        MapWindowSignature signature,
        string floor = "1f") =>
        IsValid
        && Floor == floor
        && MapId == mapId
        && MapUpdatedAt == mapUpdatedAt
        && ClientWidth == signature.ClientWidth
        && ClientHeight == signature.ClientHeight
        && ViewportWidth == signature.ViewportWidth
        && ViewportHeight == signature.ViewportHeight
        && Dpi == signature.Dpi;

    public MapAlignmentCalibration Clone() => (MapAlignmentCalibration)MemberwiseClone();
}

/// <summary>
/// A floor-specific scale relationship learned from trusted alignments.  It is
/// intentionally independent of neighbouring floors and invalidates naturally
/// whenever the map package changes.
/// </summary>
public sealed class MapFloorScaleCalibration
{
    public Guid MapId { get; set; }
    public DateTimeOffset MapUpdatedAt { get; set; }
    public string PrimaryFloorKey { get; set; } = string.Empty;
    public string FloorKey { get; set; } = string.Empty;
    public List<double> RecentTrustedRatios { get; set; } = [];
    public int TotalSampleCount { get; set; }
    public double MedianRatio { get; set; }
    /// <summary>Median absolute deviation expressed relative to the median.</summary>
    public double MedianAbsoluteDeviation { get; set; }
    public double LastConfidence { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        MapId != Guid.Empty
        && MapUpdatedAt != default
        && !string.IsNullOrWhiteSpace(PrimaryFloorKey)
        && !string.IsNullOrWhiteSpace(FloorKey)
        && !string.Equals(PrimaryFloorKey, FloorKey, StringComparison.Ordinal)
        && RecentTrustedRatios is { Count: > 0 }
        && RecentTrustedRatios.All(IsValidRatio)
        && IsValidRatio(MedianRatio)
        && double.IsFinite(MedianAbsoluteDeviation)
        && MedianAbsoluteDeviation >= 0d;

    public bool Matches(
        Guid mapId,
        DateTimeOffset mapUpdatedAt,
        string primaryFloorKey,
        string floorKey) =>
        IsValid
        && MapId == mapId
        && MapUpdatedAt == mapUpdatedAt
        && string.Equals(PrimaryFloorKey, primaryFloorKey, StringComparison.Ordinal)
        && string.Equals(FloorKey, floorKey, StringComparison.Ordinal);

    public bool TryAddTrustedSample(
        double ratio,
        double confidence,
        DateTimeOffset observedAt,
        out string? rejectionReason)
    {
        rejectionReason = null;
        Normalize();
        if (!IsValidRatio(ratio))
        {
            rejectionReason = "invalid-scale-ratio";
            return false;
        }

        if (RecentTrustedRatios.Count >= 3)
        {
            var relativeDeviation = Math.Abs(ratio - MedianRatio) / MedianRatio;
            var threshold = Math.Max(0.08d, 3d * MedianAbsoluteDeviation);
            if (relativeDeviation > threshold)
            {
                rejectionReason =
                    $"ratio-outlier:{relativeDeviation:F6}>{threshold:F6}";
                return false;
            }
        }

        RecentTrustedRatios.Add(ratio);
        if (RecentTrustedRatios.Count > 7)
            RecentTrustedRatios.RemoveRange(0, RecentTrustedRatios.Count - 7);
        TotalSampleCount = Math.Max(TotalSampleCount, 0) + 1;
        LastConfidence = double.IsFinite(confidence)
            ? Math.Clamp(confidence, 0d, 1d)
            : 0d;
        UpdatedAt = observedAt;
        RecalculateStatistics();
        return true;
    }

    public void Normalize()
    {
        RecentTrustedRatios ??= [];
        RecentTrustedRatios = RecentTrustedRatios
            .Where(IsValidRatio)
            .TakeLast(7)
            .ToList();
        TotalSampleCount = Math.Max(TotalSampleCount, RecentTrustedRatios.Count);
        LastConfidence = double.IsFinite(LastConfidence)
            ? Math.Clamp(LastConfidence, 0d, 1d)
            : 0d;
        RecalculateStatistics();
    }

    public MapFloorScaleCalibration Clone() => new()
    {
        MapId = MapId,
        MapUpdatedAt = MapUpdatedAt,
        PrimaryFloorKey = PrimaryFloorKey,
        FloorKey = FloorKey,
        RecentTrustedRatios = [.. RecentTrustedRatios],
        TotalSampleCount = TotalSampleCount,
        MedianRatio = MedianRatio,
        MedianAbsoluteDeviation = MedianAbsoluteDeviation,
        LastConfidence = LastConfidence,
        UpdatedAt = UpdatedAt
    };

    private void RecalculateStatistics()
    {
        if (RecentTrustedRatios.Count == 0)
        {
            MedianRatio = 0d;
            MedianAbsoluteDeviation = 0d;
            return;
        }

        MedianRatio = Median(RecentTrustedRatios);
        MedianAbsoluteDeviation = Median(
            RecentTrustedRatios.Select(value =>
                Math.Abs(value - MedianRatio) / MedianRatio));
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }

    private static bool IsValidRatio(double value) =>
        double.IsFinite(value) && value > 0d;
}

public static class MapFloorScaleSearchPolicy
{
    public static (double InitialRadius, double ExpandedRadius) GetRadii(
        bool hasFloorCalibration) =>
        hasFloorCalibration
            ? (0.04d, 0.15d)
            : (0.15d, 0.30d);
}

public sealed class MapPlayerState
{
    public PlayerSlot PlayerSlot { get; init; }
    public MapViewportPoint ViewportPoint { get; init; }
    public MapScreenPoint ScreenPoint { get; init; }
    public MapReferencePoint ReferencePoint { get; init; }
    public double MarkerWidth { get; init; }
    public double MarkerHeight { get; init; }
    public double Confidence { get; init; }
    public DateTimeOffset ObservedAt { get; init; }

    [JsonIgnore]
    public bool IsTrusted =>
        IsTrustedAt(MapSessionRules.MinimumPlayerConfidence);

    public bool IsTrustedAt(double minimumConfidence) =>
        ViewportPoint.IsFinite
        && ScreenPoint.IsFinite
        && ReferencePoint.IsFinite
        && Enum.IsDefined(PlayerSlot)
        && double.IsFinite(MarkerWidth)
        && MarkerWidth > 0d
        && double.IsFinite(MarkerHeight)
        && MarkerHeight > 0d
        && Confidence >= minimumConfidence;
}

public sealed class MapSessionSnapshot
{
    public int Version { get; init; }
    /// <summary>
    /// Changes only when the alignment lock itself is created, updated,
    /// invalidated, or closed. Player-only updates preserve this value.
    /// </summary>
    public long AlignmentRevision { get; init; }
    public Guid? MapId { get; init; }
    public string? Floor { get; init; }
    public MapSessionState State { get; init; } = MapSessionState.Closed;
    public MapLocationMethod LocationMethod { get; init; }
    public MapRecalibrationReason RecalibrationReason { get; init; }
    public MapViewportOrigin? ViewportOrigin { get; init; }
    public MapSimilarityTransform? LockedTransform { get; init; }
    public MapPlayerState? Player { get; init; }
    public double Confidence { get; init; }
    public int StableCandidateFrames { get; init; }
    public string Detail { get; init; } = string.Empty;

    [JsonIgnore]
    public bool IsLocked =>
        State == MapSessionState.Locked
        && LockedTransform?.IsValid is true;
}

public sealed class MapOpenSession
{
    private int _version;
    private long _alignmentRevision;

    public MapSessionSnapshot Snapshot { get; private set; } = new();

    public MapSessionSnapshot Transition(
        MapSessionState state,
        Guid? mapId = null,
        string? floor = null,
        MapLocationMethod locationMethod = MapLocationMethod.None,
        MapRecalibrationReason reason = MapRecalibrationReason.None,
        MapViewportOrigin? viewportOrigin = null,
        MapSimilarityTransform? lockedTransform = null,
        MapPlayerState? player = null,
        double confidence = 0d,
        int stableCandidateFrames = 0,
        string? detail = null)
    {
        if (!MapSessionRules.IsValidTransition(Snapshot.State, state))
        {
            throw new InvalidOperationException(
                $"Invalid map session transition: {Snapshot.State} -> {state}.");
        }

        var resolvedMapId = mapId ?? Snapshot.MapId;
        var resolvedFloor = floor ?? Snapshot.Floor;
        if (Snapshot.IsLocked
            && state == MapSessionState.Locked
            && (resolvedMapId != Snapshot.MapId
                || !string.Equals(
                    resolvedFloor,
                    Snapshot.Floor,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "A locked map or floor cannot be replaced without entering recalibration first.");
        }
        var preservesLockedPlayer = state == MapSessionState.Locked
            && Snapshot.IsLocked
            && resolvedMapId == Snapshot.MapId
            && string.Equals(
                resolvedFloor,
                Snapshot.Floor,
                StringComparison.Ordinal);
        var resolvedLockedTransform = state == MapSessionState.Locked
            ? lockedTransform
                ?? (preservesLockedPlayer ? Snapshot.LockedTransform : null)
            : null;
        if (state == MapSessionState.Locked
            && resolvedLockedTransform?.IsValid is not true)
        {
            throw new InvalidOperationException(
                "Entering a locked state requires a newly validated transform.");
        }

        var changesAlignmentRevision = state == MapSessionState.Closed
            || Snapshot.IsLocked
            || state == MapSessionState.Locked;
        var alignmentRevision = changesAlignmentRevision
            ? ++_alignmentRevision
            : _alignmentRevision;
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = alignmentRevision,
            MapId = resolvedMapId,
            Floor = resolvedFloor,
            State = state,
            LocationMethod = locationMethod == MapLocationMethod.None
                ? Snapshot.LocationMethod
                : locationMethod,
            RecalibrationReason = reason,
            ViewportOrigin = state == MapSessionState.Locked
                ? viewportOrigin
                    ?? (preservesLockedPlayer ? Snapshot.ViewportOrigin : null)
                : null,
            LockedTransform = resolvedLockedTransform,
            Player = state == MapSessionState.Locked
                ? player ?? (preservesLockedPlayer ? Snapshot.Player : null)
                : null,
            Confidence = Math.Clamp(
                double.IsFinite(confidence) ? confidence : 0d,
                0d,
                1d),
            StableCandidateFrames = Math.Max(0, stableCandidateFrames),
            Detail = detail ?? string.Empty
        };
        return Snapshot;
    }

    public MapSessionSnapshot UpdatePlayer(MapPlayerState? player)
    {
        if (!Snapshot.IsLocked)
            return Snapshot;
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = Snapshot.AlignmentRevision,
            MapId = Snapshot.MapId,
            Floor = Snapshot.Floor,
            State = Snapshot.State,
            LocationMethod = Snapshot.LocationMethod,
            RecalibrationReason = Snapshot.RecalibrationReason,
            ViewportOrigin = Snapshot.ViewportOrigin,
            LockedTransform = Snapshot.LockedTransform,
            Player = player,
            Confidence = Snapshot.Confidence,
            StableCandidateFrames = Snapshot.StableCandidateFrames,
            Detail = Snapshot.Detail
        };
        return Snapshot;
    }

    /// <summary>
    /// Commits a newly trusted alignment observation without rebuilding the
    /// map-open session or discarding the current player observation.
    /// </summary>
    public MapSessionSnapshot UpdateLockedAlignment(
        Guid mapId,
        string floor,
        MapLocationMethod locationMethod,
        MapViewportOrigin viewportOrigin,
        MapSimilarityTransform lockedTransform,
        double confidence,
        int stableCandidateFrames,
        string? detail = null)
    {
        if (!Snapshot.IsLocked)
        {
            throw new InvalidOperationException(
                "Alignment observations can update only a locked map session.");
        }
        if (Snapshot.MapId != mapId
            || !string.Equals(Snapshot.Floor, floor, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Alignment observations cannot change the locked map or floor.");
        }
        if (!lockedTransform.IsValid)
        {
            throw new InvalidOperationException(
                "The alignment observation transform is not valid.");
        }

        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            MapId = Snapshot.MapId,
            Floor = Snapshot.Floor,
            State = MapSessionState.Locked,
            LocationMethod = locationMethod == MapLocationMethod.None
                ? Snapshot.LocationMethod
                : locationMethod,
            RecalibrationReason = MapRecalibrationReason.None,
            ViewportOrigin = viewportOrigin,
            LockedTransform = lockedTransform,
            Player = Snapshot.Player,
            Confidence = Math.Clamp(
                double.IsFinite(confidence) ? confidence : 0d,
                0d,
                1d),
            StableCandidateFrames = Math.Max(0, stableCandidateFrames),
            Detail = detail ?? string.Empty
        };
        return Snapshot;
    }

    public MapSessionSnapshot Close(string? detail = null)
    {
        Snapshot = new MapSessionSnapshot
        {
            Version = ++_version,
            AlignmentRevision = ++_alignmentRevision,
            State = MapSessionState.Closed,
            Detail = detail ?? string.Empty
        };
        return Snapshot;
    }
}

public sealed class MapRegistrationConfidenceEvidence
{
    public double? AnchorGeometry { get; init; }
    public double? FeatureConsensus { get; init; }
    public double? CandidateSeparation { get; init; }
    public double? StructureQuality { get; init; }
    public double? RefinementQuality { get; init; }
    public double? BoundsAndPrior { get; init; }
    public double? TemporalStability { get; init; }

    public double Calculate()
    {
        var evidence = new[]
        {
            (AnchorGeometry, 0.20d),
            (FeatureConsensus, 0.15d),
            (CandidateSeparation, 0.10d),
            (StructureQuality, 0.25d),
            (RefinementQuality, 0.10d),
            (BoundsAndPrior, 0.10d),
            (TemporalStability, 0.10d)
        };
        var available = evidence
            .Where(item => item.Item1 is { } value && double.IsFinite(value))
            .ToArray();
        if (available.Length == 0)
            return 0d;
        var weight = available.Sum(item => item.Item2);
        return Math.Clamp(
            available.Sum(item => Math.Clamp(item.Item1!.Value, 0d, 1d) * item.Item2)
                / weight,
            0d,
            1d);
    }
}

public sealed class MapCandidateStabilityTracker
{
    private MapSimilarityTransform? _candidate;
    private int _count;
    private readonly List<MapSimilarityTransform> _history = [];

    public int Count => _count;
    public IReadOnlyList<MapSimilarityTransform> History =>
        _history.ToArray();

    public bool Observe(MapSimilarityTransform candidate, double tolerancePixels = 3d)
    {
        if (!candidate.IsValid)
        {
            Reset();
            return false;
        }

        if (_candidate is null
            || Math.Abs(_candidate.TranslationX - candidate.TranslationX) > tolerancePixels
            || Math.Abs(_candidate.TranslationY - candidate.TranslationY) > tolerancePixels
            || Math.Abs((_candidate.Scale / candidate.Scale) - 1d) > 0.003d
            || Math.Abs(_candidate.RotationDegrees - candidate.RotationDegrees) > 0.1d)
        {
            _candidate = candidate;
            _count = 1;
            _history.Clear();
        }
        else
        {
            _candidate = candidate;
            _count++;
        }
        _history.Add(candidate);
        if (_history.Count > 5)
            _history.RemoveAt(0);
        return _count >= MapSessionRules.MediumConfidenceConfirmationFrames;
    }

    public void Reset()
    {
        _candidate = null;
        _count = 0;
        _history.Clear();
    }
}

/// <summary>
/// Debounces passive floor observations before they are allowed to invalidate
/// a trusted alignment. A missing or matching observation breaks the streak.
/// </summary>
public sealed class MapFloorChangeTracker
{
    private string? _candidateFloor;

    public int Count { get; private set; }
    public string? CandidateFloor => _candidateFloor;

    public bool Observe(
        string? lockedFloor,
        string? observedFloor,
        int requiredFrames = MapSessionRules.BackgroundFailureFrames)
    {
        if (string.IsNullOrWhiteSpace(lockedFloor)
            || string.IsNullOrWhiteSpace(observedFloor)
            || string.Equals(
                lockedFloor,
                observedFloor,
                StringComparison.Ordinal))
        {
            Reset();
            return false;
        }

        if (!string.Equals(
                _candidateFloor,
                observedFloor,
                StringComparison.Ordinal))
        {
            _candidateFloor = observedFloor;
            Count = 1;
        }
        else
        {
            Count++;
        }
        return Count >= Math.Max(1, requiredFrames);
    }

    public void Reset()
    {
        _candidateFloor = null;
        Count = 0;
    }
}

/// <summary>
/// Prevents an older dispatcher callback from rendering after a newer
/// alignment observation or session invalidation has already won.
/// </summary>
public sealed class MapAlignmentCommitGuard
{
    private readonly object _gate = new();
    private long _generation;

    public long BeginCommit()
    {
        lock (_gate)
            return ++_generation;
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
            return generation > 0 && _generation == generation;
    }

    public bool TryInvalidate(long generation)
    {
        lock (_gate)
        {
            if (generation <= 0 || _generation != generation)
                return false;
            _generation++;
            return true;
        }
    }

    public bool TryCommit(long generation, Action commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        lock (_gate)
        {
            if (generation <= 0 || _generation != generation)
                return false;
            commit();
            return true;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
            _generation++;
    }
}

public static class MapSessionRules
{
    public const double HighConfidence = 0.82d;
    public const double MediumConfidence = 0.62d;
    public const double NativeScaleChangeRatio = 0.03d;
    public const double MinimumPlayerConfidence =
        PlayerTrackingRules.DefaultMinimumConfidence;
    public const int MediumConfidenceConfirmationFrames = 3;
    public const int BackgroundFailureFrames = 3;

    /// <summary>
    /// A locked transform is invalidated only after several consecutive,
    /// identity-matched contradictory tracking observations. Inconclusive
    /// searches and system failures are deliberately excluded by
    /// <see cref="MapAlignmentSession.HoldContinuousObservation"/>.
    /// </summary>
    public static bool ShouldLoseAlignmentLock(
        MapAlignmentSession? session,
        int requiredContradictoryFrames = BackgroundFailureFrames) =>
        session is not null
        && session.ConsecutiveRejections
            >= Math.Max(1, requiredContradictoryFrames);

    /// <summary>
    /// Passive visual checks may validate or close an existing map session,
    /// but they are not authorized to create or retry one. Only an explicit
    /// game-map input or explicitly requested scan can start recognition.
    /// </summary>
    public static bool ShouldMonitorVisualPresence(MapSessionState state) =>
        state != MapSessionState.Closed;

    /// <summary>
    /// Passive validation is only meaningful for a successfully locked map.
    /// Failed or incomplete alignment states do not own a visible background,
    /// so polling their floor indicator wastes CPU and must not manufacture a
    /// close transition.
    /// </summary>
    public static bool ShouldRunPassiveSessionMonitor(
        MapSessionState state,
        bool scanInProgress) =>
        !scanInProgress && state == MapSessionState.Locked;

    public static bool CanContinueOpenPipeline(
        MapGameToggleState toggleState,
        MapGameToggleTransition transition,
        MapSessionState sessionState) =>
        sessionState != MapSessionState.Closed
        && toggleState.IsCurrent(transition);

    public static bool HasRequiredLockStability(
        double confidence,
        double highConfidence,
        bool skipStabilityConfirmation,
        int observedStableFrames,
        int requiredStableFrames) =>
        double.IsFinite(confidence)
        && (confidence >= highConfidence
            || skipStabilityConfirmation
            || observedStableFrames >= Math.Max(1, requiredStableFrames));

    public static bool IsValidTransition(
        MapSessionState current,
        MapSessionState next)
    {
        if (current == next)
            return true;
        if (next == MapSessionState.Closed)
            return true;
        return current switch
        {
            MapSessionState.Closed =>
                next == MapSessionState.OpeningDetected,
            MapSessionState.OpeningDetected =>
                next is MapSessionState.WaitingForStableFrames
                    or MapSessionState.LowConfidence,
            MapSessionState.WaitingForStableFrames =>
                next is MapSessionState.IdentifyingMap
                    or MapSessionState.LowConfidence,
            MapSessionState.IdentifyingMap =>
                next is MapSessionState.CoarseLocating
                    or MapSessionState.LowConfidence,
            MapSessionState.CoarseLocating =>
                next is MapSessionState.FineLocating
                    or MapSessionState.LowConfidence,
            MapSessionState.FineLocating =>
                next is MapSessionState.Confirming
                    or MapSessionState.Locked
                    or MapSessionState.LowConfidence,
            MapSessionState.Confirming =>
                next is MapSessionState.Confirming
                    or MapSessionState.Locked
                    or MapSessionState.LowConfidence,
            MapSessionState.Locked =>
                next is MapSessionState.Lost
                    or MapSessionState.RecalibrationRequired,
            MapSessionState.LowConfidence =>
                next is MapSessionState.WaitingForStableFrames
                    or MapSessionState.CoarseLocating
                    or MapSessionState.RecalibrationRequired,
            MapSessionState.Lost =>
                next is MapSessionState.RecalibrationRequired
                    or MapSessionState.WaitingForStableFrames,
            MapSessionState.RecalibrationRequired =>
                next is MapSessionState.WaitingForStableFrames
                    or MapSessionState.CoarseLocating,
            _ => false
        };
    }

    public static MapViewportOrigin PredictViewportOrigin(
        MapReferencePoint player,
        double viewportScreenWidth,
        double viewportScreenHeight,
        double scale,
        MapReferenceBounds bounds)
    {
        if (!player.IsFinite
            || !double.IsFinite(scale)
            || scale <= 0d)
        {
            return new MapViewportOrigin(bounds.X, bounds.Y);
        }
        var width = viewportScreenWidth / scale;
        var height = viewportScreenHeight / scale;
        return bounds.ClampViewportOrigin(
            new MapViewportOrigin(
                player.X - (width / 2d),
                player.Y - (height / 2d)),
            width,
            height);
    }

    /// <summary>
    /// Reprojects a current screen-space player observation after a trusted
    /// alignment update. Its reference coordinate belongs to the transform
    /// and cannot be carried forward unchanged.
    /// </summary>
    public static MapPlayerState? ReprojectPlayer(
        MapPlayerState? player,
        MapSimilarityTransform transform,
        MapReferenceBounds bounds)
    {
        if (player is null || !transform.IsValid || !bounds.IsValid)
            return null;
        var reference = transform.ToReference(player.ScreenPoint);
        if (!reference.IsFinite || !bounds.Contains(reference, tolerance: 1d))
            return null;
        return new MapPlayerState
        {
            PlayerSlot = player.PlayerSlot,
            ViewportPoint = player.ViewportPoint,
            ScreenPoint = player.ScreenPoint,
            ReferencePoint = bounds.Clamp(reference),
            MarkerWidth = player.MarkerWidth,
            MarkerHeight = player.MarkerHeight,
            Confidence = player.Confidence,
            ObservedAt = player.ObservedAt
        };
    }

    public static MapRecalibrationReason GetSignatureChangeReason(
        MapWindowSignature locked,
        MapWindowSignature current)
    {
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(current);
        if (locked.Dpi != current.Dpi)
            return MapRecalibrationReason.DpiChanged;
        if (locked.ClientWidth != current.ClientWidth
            || locked.ClientHeight != current.ClientHeight)
        {
            return MapRecalibrationReason.ResolutionChanged;
        }
        if (locked.ViewportWidth != current.ViewportWidth
            || locked.ViewportHeight != current.ViewportHeight)
        {
            return MapRecalibrationReason.ViewportChanged;
        }
        return MapRecalibrationReason.WindowChanged;
    }
}
