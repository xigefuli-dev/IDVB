using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static partial class MapDiagnosticModeCapture
{
    private static readonly AsyncLocal<DeferredFitnessCapture?> DeferredFitness = new();

    internal static DeferredFitnessCapture CaptureDeferredFitness()
    {
        lock (Gate)
            return new(_matchDirectory, _currentMapOpenId, _matchIdentity);
    }

    internal static bool TryDeferFitness(Func<FitnessDrawing> snapshot)
    {
        if (DeferredFitness.Value is not { } capture)
            return false;
        if (SuppressionDepth.Value == 0)
            capture.Enqueue(snapshot);
        return true;
    }

    internal static bool FlushDeferredFitness() => DeferredFitness.Value?.Flush() ?? false;

    // The closure may reference only the owned Mat snapshots and immutable
    // scalar/rectangle values, never a released floor lease or query.
    internal sealed class FitnessDrawing(Func<Mat> render, params Mat[] owned) : IDisposable
    {
        internal Mat Render() => render();
        public void Dispose()
        {
            foreach (var image in owned)
                image.Dispose();
        }
    }

    internal sealed class DeferredFitnessCapture : IDisposable
    {
        // One synchronous recognition worker owns this capture. AsyncLocal
        // propagates the destination; it does not authorize parallel producers.
        private readonly Dictionary<string, FitnessDrawing> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly IDisposable? _protection;
        private readonly object? _matchIdentity;
        private int _requests, _replacements, _drawings;
        private double _snapshotMs, _flushMs;
        private bool _disposed;
        internal string? MatchDirectory { get; }
        internal int MapOpenId { get; }

        internal DeferredFitnessCapture(string? directory, int mapOpenId, object? matchIdentity)
        {
            MatchDirectory = directory;
            MapOpenId = mapOpenId;
            _matchIdentity = matchIdentity;
            if (directory is not null && mapOpenId > 0)
                _protection = AppDataPaths.ProtectCachePath(directory);
        }

        internal IDisposable Activate()
        {
            var previous = DeferredFitness.Value;
            DeferredFitness.Value = this;
            return new Activation(this, previous);
        }

        internal void Enqueue(Func<FitnessDrawing> snapshot)
        {
            if (_disposed || MatchDirectory is null || MapOpenId <= 0)
                return;
            var timer = Stopwatch.StartNew();
            try
            {
                var drawing = snapshot();
                var path = Path.Combine(MatchDirectory, "贴合度", $"贴合度 {MapOpenId}.png");
                if (_pending.Remove(path, out var previous))
                {
                    previous.Dispose();
                    _replacements++;
                }
                _pending.Add(path, drawing);
                _requests++;
            }
            catch { /* Optional diagnostics must not change recognition. */ }
            finally { _snapshotMs += timer.Elapsed.TotalMilliseconds; }
        }

        internal void Discard(string path)
        {
            if (_pending.Remove(path, out var previous))
                previous.Dispose();
        }

        internal void WriteIfCurrent(string path, Mat image)
        {
            // Clear can reuse the same directory/open number in a new match.
            // Object identity binds this job to the original diagnostic session.
            lock (Gate)
                if (_matchIdentity is not null
                    && ReferenceEquals(_matchIdentity, MapDiagnosticModeCapture._matchIdentity))
                    TryWrite(path, image);
        }

        internal bool Flush()
        {
            var hadDrawings = _pending.Count > 0;
            var timer = Stopwatch.StartNew();
            foreach (var (path, drawing) in _pending)
            {
                using (drawing)
                {
                    try
                    {
                        lock (Gate)
                            if (_matchIdentity is null
                                || !ReferenceEquals(_matchIdentity, MapDiagnosticModeCapture._matchIdentity))
                                continue;
                        using var visual = drawing.Render();
                        // Never recreate a directory removed by Clear(). The
                        // frozen path also cannot point to a subsequent match.
                        WriteIfCurrent(path, visual);
                        _drawings++;
                    }
                    catch { /* Preserve the identity on render/write failure. */ }
                }
            }
            _pending.Clear();
            _flushMs += timer.Elapsed.TotalMilliseconds;
            return hadDrawings;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Flush(); }
            finally
            {
                _protection?.Dispose();
                if (_requests > 0)
                    try { MapLogCollector.Instance.Append(MapLogCategory.StructureRegistration,
                        MapLogLevel.Info, "自动身份诊断输出合并", details: new()
                        {
                            ["mapOpenId"] = MapOpenId, ["requests"] = _requests,
                            ["replacements"] = _replacements, ["drawings"] = _drawings,
                            ["snapshotMs"] = _snapshotMs, ["flushMs"] = _flushMs
                        }); }
                    catch { /* Diagnostics cannot fault a completed identity job. */ }
            }
        }

        private sealed class Activation(DeferredFitnessCapture owner,
            DeferredFitnessCapture? previous) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { owner.Flush(); }
                finally { DeferredFitness.Value = previous; }
            }
        }
    }
}
