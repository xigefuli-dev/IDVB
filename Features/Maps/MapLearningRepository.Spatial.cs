using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapLearningRepository
{
    public Mat LoadLiveImage(MapLearningSampleManifest sample) =>
        Cv2.ImRead(Path.Combine(SamplesDirectory, sample.SampleId,
            sample.LiveImageFile), ImreadModes.Unchanged);

    public Mat LoadReferenceImage(MapLearningCandidateManifest candidate) =>
        Cv2.ImRead(Path.Combine(ReferencesDirectory, candidate.ReferenceFile),
            ImreadModes.Unchanged);

    internal static bool TryResolveSpatialLabel(
        MapRecognitionChoice choice,
        OpenCvSharp.Size sourceSize,
        MapScreenRect? viewportBounds,
        out double normalizedX,
        out double normalizedY)
    {
        normalizedX = 0d;
        normalizedY = 0d;
        var result = choice.Recognition.Result;
        var transform = result.OverlayTransform;
        if (transform is null
            || result.LocalizationConfidence < 0.85d
            || result.EvidenceKind == MapAlignmentEvidenceKind.None
            || result.WasForcedBestResult
            || result.ReusedLastTransform
            || viewportBounds is not { IsValid: true }
            || sourceSize.Width <= 0
            || sourceSize.Height <= 0)
        {
            return false;
        }

        var referenceWidth = transform.ReferenceWidth;
        var referenceHeight = transform.ReferenceHeight;
        if (referenceWidth <= 0 || referenceHeight <= 0
            || !double.IsFinite(transform.ScaleX) || transform.ScaleX <= 0d
            || !double.IsFinite(transform.ScaleY) || transform.ScaleY <= 0d)
            return false;
        var x = (viewportBounds.Value.CenterX - transform.OffsetX)
            / transform.ScaleX / referenceWidth;
        var y = (viewportBounds.Value.CenterY - transform.OffsetY)
            / transform.ScaleY / referenceHeight;
        if (!double.IsFinite(x) || !double.IsFinite(y)
            || x is < 0d or > 1d || y is < 0d or > 1d)
        {
            return false;
        }

        var observationSize = MapLearningPreprocessor.ObservationSize;
        var scale = Math.Min(
            (double)observationSize / sourceSize.Width,
            (double)observationSize / sourceSize.Height);
        var renderedWidth = sourceSize.Width * scale;
        var renderedHeight = sourceSize.Height * scale;
        normalizedX = ((observationSize - renderedWidth) / 2d
            + x * renderedWidth) / observationSize;
        normalizedY = ((observationSize - renderedHeight) / 2d
            + y * renderedHeight) / observationSize;
        return true;
    }
}
