using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using IDVBuff.Survey.Contracts;
using IDVBuff.Survey.Domain;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

    private async Task<SurveyOperationResult<SurveyObservationCommitResult>> AddSurveyFrameAsync(
        CapturedGameFrame frame,
        MapMatchSnapshot match,
        int mapToggleVersion,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (match.SurveyProjectId is not { } projectId)
        {
            return SurveyOperationResult<SurveyObservationCommitResult>.Failure(
                SurveyErrorCode.InvalidState,
                "当前对局没有测绘项目。");
        }

        Cv2.ImEncode(".png", frame.Image, out var bytes);
        var floorKey = NormalizeSurveyFloorKey(_currentFloorKey ?? match.FloorKey);
        var capture = new SurveyCaptureContext(
            match.MatchId,
            match.OperationEpoch,
            mapToggleVersion,
            DateTimeOffset.UtcNow,
            (int)Math.Round(frame.ClientBounds.Width),
            (int)Math.Round(frame.ClientBounds.Height),
            DwrGameWindowCaptureService.GetWindowDpi(frame.WindowHandle),
            new SurveyPixelRect(
                frame.ViewportBounds.X,
                frame.ViewportBounds.Y,
                frame.ViewportBounds.Width,
                frame.ViewportBounds.Height),
            floorKey,
            CreateSurveyConfigDigest(),
            GetSurveyAlgorithmVersion());
        return await _surveyCoordinator.AddObservationAsync(
            new SurveyObservationRequest(
                Guid.NewGuid(),
                projectId,
                expectedRevision,
                new SurveyEncodedFrame(
                    bytes,
                    ".png",
                    "image/png",
                    frame.Image.Width,
                    frame.Image.Height,
                    capture)),
            cancellationToken);
    }

    private string CreateSurveyConfigDigest()
    {
        var preprocessing = _config.Get<SurveyPreprocessingTuning>("survey.preprocessing");
        var registration = _config.Get<SurveyRegistrationTuning>("survey.registration");
        var storage = _config.Get<SurveyStorageTuning>("survey.storage");
        var visual = _config.Get<SurveyFusionTuning>("survey.fusion.visual");
        var structure = _config.Get<SurveyFusionTuning>("survey.fusion.structure");
        var payload = string.Join('|',
            "survey-schema-1",
            _config.ActiveResolutionPreset,
            Invariant(_surveyCaptureTuning.StableFrameDelayMilliseconds),
            Invariant(_surveyCaptureTuning.MaximumCaptureMilliseconds),
            Invariant(_surveyCaptureTuning.QueueCapacity),
            Invariant(preprocessing.MaximumFeatureCount),
            Invariant(preprocessing.EdgeLowThreshold),
            Invariant(preprocessing.EdgeHighThreshold),
            Invariant(preprocessing.MapCanvasLeft),
            Invariant(preprocessing.MapCanvasTop),
            Invariant(preprocessing.MapCanvasRight),
            Invariant(preprocessing.MapCanvasBottom),
            Invariant(preprocessing.ShapeOpeningDivisor),
            Invariant(preprocessing.ShapeClosingDivisor),
            Invariant(preprocessing.MinimumShapeComponentAreaRatio),
            Invariant(preprocessing.MinimumShapeThicknessFactor),
            Invariant(preprocessing.MaximumShapeHoleAreaRatio),
            Invariant(registration.CandidateCount),
            Invariant(registration.RatioTest),
            Invariant(registration.MinimumMatches),
            Invariant(registration.MinimumInliers),
            Invariant(registration.MinimumInlierRatio),
            Invariant(registration.MaximumResidualPixels),
            Invariant(registration.MinimumScale),
            Invariant(registration.MaximumScale),
            Invariant(storage.AssetRetentionDays),
            Invariant(storage.MaximumProjectLayers),
            Invariant(visual.MaximumOutputPixels),
            Invariant(structure.StructureBinaryThreshold));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        static string Invariant(object value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private async Task RecordSurveyCaptureFailureAsync(
        MapMatchSnapshot match,
        long toggleVersion,
        string message)
    {
        if (match.SurveyProjectId is not { } projectId)
            return;
        var snapshot = await _surveyCoordinator.GetProjectAsync(projectId, CancellationToken.None);
        if (snapshot is null)
            return;
        await _surveyCoordinator.RecordCaptureFailureAsync(
            new SurveyCaptureFailureRequest(
                Guid.NewGuid(),
                projectId,
                snapshot.Project.Revision,
                match.MatchId,
                match.OperationEpoch,
                toggleVersion,
                NormalizeSurveyFloorKey(_currentFloorKey ?? match.FloorKey),
                DateTimeOffset.UtcNow,
                SurveyErrorCode.CaptureFailed,
                message),
            CancellationToken.None);
    }

    private static string GetSurveyAlgorithmVersion() =>
        typeof(SessionOrchestrator).Assembly.GetName().Version?.ToString()
        ?? "1.0.0";

    private static string NormalizeSurveyFloorKey(string? floorKey) =>
        string.IsNullOrWhiteSpace(floorKey) ? "1f" : floorKey.Trim().ToLowerInvariant();
}
