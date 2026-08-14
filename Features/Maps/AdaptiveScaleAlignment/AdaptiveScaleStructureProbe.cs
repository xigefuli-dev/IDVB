namespace IDVBuff.Features.Maps.AdaptiveScaleAlignment;

/// <summary>
/// Orchestrates fine scale passes while the existing registrar owns feature
/// extraction, scoring, validation and translation refinement.
/// </summary>
internal sealed class AdaptiveScaleStructureProbe
{
    public MapRecognitionAttempt Refine(
        RuntimeMapRecognition seed,
        Func<RuntimeMapRecognition, double, MapRecognitionAttempt> runPass)
    {
        var first = runPass(seed, 0.02d);
        if (first.Recognition is not { } firstRecognition)
            return first;

        var second = runPass(firstRecognition, 0.005d);
        return second.Recognition is not null ? second : first;
    }
}
