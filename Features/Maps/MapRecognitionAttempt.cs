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
    public double RawConfidence => Recognition.Result.Confidence;
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
