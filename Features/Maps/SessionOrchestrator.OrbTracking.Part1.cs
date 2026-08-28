using IDVBuff.Core.Models;
using OpenCvSharp;
using System.Diagnostics;
using IDVBuff.Features.Maps.AdaptiveScaleAlignment;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

    private void CancelOrbTracking(string reason)
    {
        Interlocked.Increment(ref _orbTrackingGeneration);
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_orbTrackingGate)
        {
            cancellation = _orbTrackingCancellation;
            task = _orbTrackingTask;
            if (task is not null)
                _retiredOrbTrackingTask = task;
            _orbTrackingCancellation = null;
            _orbTrackingTask = null;
        }
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cancellation?.Dispose();
        if (task is not null)
        {
            _logCollector.Append(
                MapLogCategory.OrbTracking,
                MapLogLevel.Info,
                $"ORB tracking invalidated · reason={reason}");
        }
    }

    private async Task DrainOrbTrackingAsync()
    {
        Task? task;
        Task? retired;
        lock (_orbTrackingGate)
        {
            task = _orbTrackingTask;
            retired = _retiredOrbTrackingTask;
        }
        if (task is null && retired is null)
            return;
        try
        {
            await Task.WhenAll(
                new[] { task, retired }.OfType<Task>());
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_orbTrackingGate)
            {
                if (_retiredOrbTrackingTask == retired)
                    _retiredOrbTrackingTask = null;
            }
        }
    }

}
