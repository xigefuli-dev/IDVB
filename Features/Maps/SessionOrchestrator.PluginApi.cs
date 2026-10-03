using System.Diagnostics;
using IdentityVisionBridge.Vision;

namespace IDVBuff.Features.Maps;

public sealed partial class SessionOrchestrator
{
    private static readonly AsyncLocal<CancellationToken> ExternalOperationCancellation = new();
    private bool _pluginOperationActive;
    private static readonly AsyncLocal<PluginAlignmentReceipt?> ExternalAlignmentReceipt = new();
    private sealed class PluginAlignmentReceipt
    {
        public long Revision;
        public Guid MapId;
        public string? FloorKey;
    }

    public Task<T> InvokePluginOperationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasThreadAccess) return operation();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        })) completion.TrySetException(new InvalidOperationException("Host dispatcher is unavailable."));
        return completion.Task;
    }

    public HostVisionSnapshot GetPluginSnapshot()
    {
        var session = SessionSnapshot;
        return new()
        {
            IsMatchStarted = IsMatchStarted, MatchId = CurrentMatchId, MapClass = MatchSnapshot.MapClass,
            IsScanning = IsScanning, IsMapOpen = _gameMapToggleState.IsOpen,
            IsOverlayVisible = _overlay.IsVisible, SelectedMapClass = LastSelectedMapClass,
            IsIdentityLocked = session.IsIdentityLocked, HasTrustedAlignment = session.IsLocked,
            MapId = session.MapId, FloorKey = session.Floor, AlignmentRevision = session.AlignmentRevision,
            Transform = session.IsLocked ? VisionApiMapper.Transform(LastRecognition?.Result.OverlayTransform)
                ?? VisionApiMapper.Transform(session.LockedTransform) : null,
            StatusMessage = StatusMessage
        };
    }

    public async Task<VisionScanResult> RunPluginScanAsync(HostScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Host scan must run on the dispatcher.");
        var operationId = Guid.NewGuid().ToString("N");
        if (_disposed || !_initialized || !_settings!.IsEnabled || !IsMatchStarted)
            return new() { OperationId = operationId, Outcome = VisionOutcome.NotReady, Reason = "host-not-ready-or-match-not-started" };
        if (PluginControlBusy)
            return new() { OperationId = operationId, Outcome = VisionOutcome.Busy, Reason = "host-operation-in-progress" };
        _pluginOperationActive = true;
        var previousCancellation = ExternalOperationCancellation.Value;
        ExternalOperationCancellation.Value = cancellationToken;
        var matchVersion = MatchSnapshot.Version;
        var revision = SessionSnapshot.AlignmentRevision;
        var timer = Stopwatch.StartNew();
        using var input = new MapInputOperationContext();
        try
        {
            await RunQuickScanAsync(new ApiCandidateSelector(request.SelectedMapId));
            cancellationToken.ThrowIfCancellationRequested();
            var committed = input.Outcome == "success" && MatchSnapshot.Version == matchVersion
                && SessionSnapshot.IsLocked && SessionSnapshot.AlignmentRevision > revision;
            var recognition = committed ? LastRecognition : null;
            return new()
            {
                OperationId = input.ScanId ?? operationId,
                Outcome = committed ? VisionOutcome.Succeeded : _lastCandidateChoices.Count > 0 ? VisionOutcome.NeedsSelection
                    : input.Reason.Contains("deadline", StringComparison.OrdinalIgnoreCase) ? VisionOutcome.TimedOut : VisionOutcome.Rejected,
                MapId = recognition?.Map.Id, FloorKey = recognition?.Result.Floor,
                Confidence = recognition?.Result.IdentityConfidence ?? 0,
                Transform = VisionApiMapper.Transform(recognition?.Result.OverlayTransform),
                Candidates = Array.AsReadOnly(_lastCandidateChoices.Select(choice => new VisionCandidate
                {
                    MapId = choice.Recognition.Map.Id, DisplayName = choice.Recognition.Map.DisplayName,
                    FloorKey = choice.Recognition.Result.Floor, Score = choice.RawConfidence,
                    EvidenceState = choice.IsReferenceOnly ? "Unverified" : "Supported",
                    Reason = choice.EvidenceLabel
                }).ToArray()),
                CommittedToHost = committed, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                Reason = input.Reason
            };
        }
        finally
        {
            ExternalOperationCancellation.Value = previousCancellation;
            _pluginOperationActive = false;
        }
    }

    public async Task<VisionAlignmentResult> RunPluginAlignmentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Host alignment must run on the dispatcher.");
        var operationId = Guid.NewGuid().ToString("N");
        if (_disposed || !_initialized || !_settings!.IsEnabled || !IsMatchStarted || !_gameMapToggleState.IsOpen
            || (LastRecognition is null && PendingAlignmentIdentity is null))
            return new() { OperationId = operationId, Outcome = VisionOutcome.NotReady, Reason = "host-or-identity-or-open-map-not-ready" };
        if (PluginControlBusy)
            return new() { OperationId = operationId, Outcome = VisionOutcome.Busy, Reason = "host-operation-in-progress" };
        _pluginOperationActive = true;
        var previousCancellation = ExternalOperationCancellation.Value;
        ExternalOperationCancellation.Value = cancellationToken;
        var revision = SessionSnapshot.AlignmentRevision;
        var matchVersion = MatchSnapshot.Version;
        var timer = Stopwatch.StartNew();
        var previousReceipt = ExternalAlignmentReceipt.Value;
        var receipt = new PluginAlignmentReceipt();
        ExternalAlignmentReceipt.Value = receipt;
        try
        {
            await RunAlignmentAsync();
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = SessionSnapshot;
            var committed = snapshot.IsLocked && receipt.Revision > revision
                && snapshot.AlignmentRevision == receipt.Revision && snapshot.MapId == receipt.MapId
                && snapshot.Floor == receipt.FloorKey && MatchSnapshot.Version == matchVersion;
            return new()
            {
                OperationId = operationId, Outcome = committed ? VisionOutcome.Succeeded : VisionOutcome.Rejected,
                MapId = snapshot.MapId, FloorKey = snapshot.Floor,
                Confidence = committed ? snapshot.Confidence : 0,
                Transform = committed ? VisionApiMapper.Transform(LastRecognition?.Result.OverlayTransform) : null,
                CommittedToHost = committed, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                Reason = committed ? "alignment-committed" : StatusMessage
            };
        }
        finally
        {
            ExternalAlignmentReceipt.Value = previousReceipt;
            ExternalOperationCancellation.Value = previousCancellation;
            _pluginOperationActive = false;
        }
    }

    private sealed class ApiCandidateSelector(Guid? selectedMapId) : IMapCandidateSelector
    {
        public Task<MapCandidateDecision> SelectAsync(CapturedGameFrame frame,
            IReadOnlyList<MapRecognitionChoice> candidates, string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = selectedMapId is { } id ? candidates.ToList().FindIndex(choice => choice.Recognition.Map.Id == id) : -1;
            return Task.FromResult(index >= 0 ? MapCandidateDecision.SelectKnownMap(index) : MapCandidateDecision.Cancel());
        }
    }
}
