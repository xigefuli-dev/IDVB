using IDVBuff.Features.Maps;

namespace IDVBuff.Tests;

[Trait("Category", "IssueRegression")]
[Trait("Issue", "13")]
public sealed class MapOpenCancellationOwnerTests
{
    [Fact]
    public async Task IdentityCommitKeepsSameOwnerForCurrentFrameCapture()
    {
        var owner = new MapOpenCancellationOwner();
        var scope = owner.Begin(default, default);
        try
        {
            owner.CancelUnlessOwnedBy(scope.Token);
            var captures = 0;
            await Task.Run(() => { scope.Token.ThrowIfCancellationRequested(); captures++; }, scope.Token);
            Assert.Equal(1, captures);
            owner.CancelUnlessOwnedBy(scope.Token); // a second explicit chooser in the same consumption
            Assert.False(scope.IsCancellationRequested);
        }
        finally { owner.Complete(scope); }
    }

    [Fact]
    public void ExternalIdentityChangeStillCancelsCurrentConsumer()
    {
        var owner = new MapOpenCancellationOwner();
        var scope = owner.Begin(default, default);
        try
        {
            owner.CancelUnlessOwnedBy();
            Assert.True(scope.IsCancellationRequested);
            Assert.Throws<OperationCanceledException>(() => owner.CancelUnlessOwnedBy(scope.Token));
        }
        finally { owner.Complete(scope); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MatchAndExternalCancellationCannotBePreserved(bool endMatch)
    {
        using var match = new CancellationTokenSource();
        using var external = new CancellationTokenSource();
        var owner = new MapOpenCancellationOwner();
        var scope = owner.Begin(match.Token, external.Token);
        try
        {
            (endMatch ? match : external).Cancel();
            Assert.Throws<OperationCanceledException>(() => owner.CancelUnlessOwnedBy(scope.Token));
        }
        finally { owner.Complete(scope); }
    }

    [Fact]
    public void OldFinallyCannotClearOrCancelNewOwner()
    {
        var owner = new MapOpenCancellationOwner();
        var old = owner.Begin(default, default);
        var oldToken = old.Token;
        var current = owner.Begin(default, default);
        try
        {
            Assert.True(oldToken.IsCancellationRequested);
            owner.Complete(old);
            Assert.Throws<OperationCanceledException>(() => owner.CancelUnlessOwnedBy(oldToken));
            owner.CancelUnlessOwnedBy(current.Token);
            Assert.False(current.IsCancellationRequested);
            owner.CancelUnlessOwnedBy();
            Assert.True(current.IsCancellationRequested);
        }
        finally { owner.Complete(current); }
    }

    [Fact]
    public void ForeignUncancelledTokenCannotClaimCurrentConsumer()
    {
        using var foreign = new CancellationTokenSource();
        var owner = new MapOpenCancellationOwner();
        var scope = owner.Begin(default, default);
        try
        {
            Assert.Throws<OperationCanceledException>(() => owner.CancelUnlessOwnedBy(foreign.Token));
            Assert.False(scope.IsCancellationRequested);
        }
        finally { owner.Complete(scope); }
    }
}
