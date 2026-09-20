using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

public sealed partial class LowStructureAlignmentPlanTests
{
    [Fact]
    public void RecoveryStartsWhereFailedQueryCanFitReference()
    {
        var failed = new MapStructureRegistrationResult
        {
            ReferenceWidth = 1451,
            ReferenceHeight = 1266,
            QueryBoundsWidth = 411,
            QueryBoundsHeight = 397,
            Candidates =
            [
                new MapStructureCandidate { Scale = 1.809834d }
            ]
        };

        var minimum = LowStructureAlignmentPlan.ResolveRecoveryMinimumScale(
            failed,
            configuredMinimumScale: 0.40d);

        Assert.InRange(minimum, 0.57d, 0.58d);
    }
}
