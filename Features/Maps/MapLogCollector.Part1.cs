using System.Diagnostics;
using System.Globalization;
using IDVBuff.Core.Diagnostics;
using IDVBuff.Diagnostics;

namespace IDVBuff.Features.Maps;
/// <summary>
/// Thread-safe, opt-in collector for structured map diagnostics.
/// A session is flushed in small batches and finalized when collection stops.
/// </summary>
public sealed partial class MapLogCollector : IDisposable, IAsyncDisposable
{
    private static Dictionary<string, object?>? EnrichDiagnosticDetails(Dictionary<string, object?>? details)
    {
        if (MapInputOperationContext.Current is { } input)
        {
            details = details is null ? new() : new(details);
            details.TryAdd("inputOperationId", input.Id);
        }
        if (ScanRequestDiagnostics.Current is { } request)
        {
            details = details is null ? new() : new(details);
            if (details.GetValueOrDefault("scanId") is null) details["scanId"] = request.ScanId;
            if (Equals(details["scanId"], request.ScanId)) details["requestStage"] = request.Stage;
        }
        if (ScanExecutionContext.Current is { } scan)
        {
            details = details is null ? new() : new(details);
            if (details.GetValueOrDefault("scanId") is null) details["scanId"] = scan.ScanId;
            if (!Equals(details["scanId"], scan.ScanId)) return details;
            details["scanElapsedMs"] = scan.ElapsedMilliseconds;
            details["scanRemainingMs"] = scan.RemainingMilliseconds;
            details["scanCancelled"] = scan.CancellationToken.IsCancellationRequested;
            details["scanSuperseded"] = scan.IsSuperseded;
        }
        return details;
    }


    public bool DisablePersistence { get; set; }

    public void AppendStatus(
        IdvbStatus status,
        MapLogCategory category = MapLogCategory.StructureRegistration,
        double? elapsedMs = null,
        Dictionary<string, object?>? details = null)
    {
        var level = status.IsServerError ? MapLogLevel.Error
            : (status.IsFallback || status.IsClientError) ? MapLogLevel.Warning
            : MapLogLevel.Info;

        details = EnrichDiagnosticDetails(details);
        var message = status.ToTraceString();
        WritePlainTextOutput(category, level, message, elapsedMs, details);
        lock (_stateGate)
        {
            if (!_isEnabled || _session is null)
                return;
            AppendInternal(
                _session,
                category,
                level,
                message,
                elapsedMs,
                details,
                writePlainTextOutput: false,
                statusCode: status.Code,
                subCode: status.SubCode,
                statusChain: status.Cause is not null ? status.ToTraceString() : null);
        }
    }

    private static void WritePlainTextOutput(
        MapLogCategory category,
        MapLogLevel level,
        string message,
        double? elapsedMs,
        Dictionary<string, object?>? details)
    {
        try
        {
            var outputMessage = message;
            if (elapsedMs is not null)
            {
                outputMessage += $" | elapsedMs="
                    + elapsedMs.Value.ToString("0.###", CultureInfo.InvariantCulture);
            }
            if (details is not null)
            {
                foreach (var detail in details)
                {
                    outputMessage += $" | {detail.Key}="
                        + FormatDetail(detail.Value);
                }
            }
            OutputLog.Write(level.ToString(), $"MAP/{category}", outputMessage);
        }
        catch
        {
            // The plain-text logging side channel must never affect map recognition.
        }
    }

    private static string FormatDetail(object? value)
    {
        if (value is null) return "null";
        if (value is string or ValueType) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        try { return System.Text.Json.JsonSerializer.Serialize(value, DetailJsonOptions); }
        catch { return $"<unserializable:{value.GetType().FullName}>"; }
    }

    private void WriteErrorToFile(string context, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(_repository.LogDirectory);
            var errorPath = Path.Combine(_repository.LogDirectory, "flush-errors.log");
            var line = $"[{DateTimeOffset.UtcNow:O}] [{context}] "
                + $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}"
                + $"{exception.StackTrace}{Environment.NewLine}";
            File.AppendAllText(errorPath, line);
        }
        catch
        {
            // Logging must never take down the application.
        }
    }

    private void DisposeTimerLocked()
    {
        var timer = Interlocked.Exchange(ref _flushTimer, null);
        if (timer is null)
            return;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
    }

    private sealed class Session(string path)
    {
        public readonly object Gate = new();
        public readonly object FlushTaskGate = new();
        public readonly string Path = path;
        public readonly List<MapLogEntry> Entries = [];
        public Task PendingFlush = Task.CompletedTask;
        public bool FlushRequested;
        public bool FlushLoopActive;
        public volatile bool PersistenceDisabled;
        public int DroppedEntryCount;
        public int Sequence;
        public int LastFlushedSequence;
        public int TotalEntryCount;
    }
}
