using System.Diagnostics;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Shared cancellation/deadline context for synchronous OpenCV alignment.
/// It lets inner search stages observe a map-close cancellation without
/// changing every existing recognition signature.
/// </summary>
internal sealed class NoDoorAlignmentDeadline : IDisposable
{
    private static readonly AsyncLocal<NoDoorAlignmentDeadline?> Ambient = new();
    private readonly CancellationToken _parentToken;
    private readonly CancellationTokenSource _linkedCancellation;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly bool _enforceTimeBudget;
    private bool _disposed;

    public NoDoorAlignmentDeadline(
        CancellationToken parentToken,
        int budgetMilliseconds,
        bool enforceTimeBudget = true)
    {
        _parentToken = parentToken;
        _enforceTimeBudget = enforceTimeBudget;
        BudgetMilliseconds =
            MapOpenAlignmentRouteRules.ResolveNoDoorAlignmentBudgetMilliseconds(
                budgetMilliseconds);
        _linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        if (enforceTimeBudget)
            _linkedCancellation.CancelAfter(BudgetMilliseconds);
    }

    public static NoDoorAlignmentDeadline? Current => Ambient.Value;
    internal static Task<T> RunAsync<T>(Func<T> worker, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = new NoDoorAlignmentDeadline(cancellationToken,
                MapOpenAlignmentRouteRules.MaximumNoDoorAlignmentBudgetMilliseconds,
                enforceTimeBudget: false);
            using var ambient = deadline.EnterAmbient();
            return worker();
        }, cancellationToken);
    public int BudgetMilliseconds { get; }
    public CancellationToken Token => _linkedCancellation.Token;
    public double ElapsedMilliseconds => _stopwatch.Elapsed.TotalMilliseconds;
    public int RemainingMilliseconds =>
        _linkedCancellation.IsCancellationRequested
            ? 0
            : !_enforceTimeBudget
                ? int.MaxValue
                : Math.Max(
                    0,
                    BudgetMilliseconds
                        - (int)Math.Ceiling(ElapsedMilliseconds));
    public bool IsExpired =>
        _linkedCancellation.IsCancellationRequested
        || RemainingMilliseconds <= 0;
    public bool TimedOut =>
        !_parentToken.IsCancellationRequested
        && IsExpired;

    public bool CanStartStage(
        int minimumMilliseconds =
            MapOpenAlignmentRouteRules.MinimumNoDoorStageBudgetMilliseconds) =>
        !IsExpired && RemainingMilliseconds >= minimumMilliseconds;

    public IDisposable EnterAmbient()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new AmbientLease(
            previous,
            MapNoDoorAlignmentBudgetContext.Enter(
                () => RemainingMilliseconds));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stopwatch.Stop();
        _linkedCancellation.Dispose();
    }

    private sealed class AmbientLease(
        NoDoorAlignmentDeadline? previous,
        IDisposable budgetLease) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            budgetLease.Dispose();
            Ambient.Value = previous;
        }
    }
}
