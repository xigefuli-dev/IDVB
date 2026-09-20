namespace IDVBuff.Survey.Domain;

public sealed record SurveyAssetReference(
    string Sha256,
    string RelativePath,
    string MediaType,
    long ByteLength,
    int PixelWidth,
    int PixelHeight)
{
    public bool IsValid =>
        Sha256.Length == 64
        && !string.IsNullOrWhiteSpace(RelativePath)
        && ByteLength > 0
        && PixelWidth > 0
        && PixelHeight > 0;
}

public sealed record SurveyCaptureContext(
    Guid MatchId,
    long OperationEpoch,
    long MapToggleVersion,
    DateTimeOffset CapturedAt,
    int ClientWidth,
    int ClientHeight,
    double Dpi,
    SurveyPixelRect ViewportBounds,
    string FloorKey,
    string ConfigDigest,
    string AlgorithmVersion);
