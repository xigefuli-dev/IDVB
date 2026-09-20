namespace IDVBuff.Features.Maps;

internal sealed partial record LowStructureAlignmentPlan
{
    internal static double ResolveRecoveryMinimumScale(
        MapStructureRegistrationResult? failedResult,
        double configuredMinimumScale)
    {
        var candidateScale = failedResult?.Candidates
            .Select(candidate => candidate.Scale)
            .FirstOrDefault(IsUsable) ?? 0d;
        if (!IsUsable(candidateScale)
            || failedResult!.ReferenceWidth <= 0
            || failedResult.ReferenceHeight <= 0
            || failedResult.QueryBoundsWidth <= 0
            || failedResult.QueryBoundsHeight <= 0)
        {
            return configuredMinimumScale;
        }

        var minimumFitScale = Math.Max(
            failedResult.QueryBoundsWidth * candidateScale
                / failedResult.ReferenceWidth,
            failedResult.QueryBoundsHeight * candidateScale
                / failedResult.ReferenceHeight);
        return Math.Max(configuredMinimumScale, minimumFitScale * 1.01d);
    }
}
