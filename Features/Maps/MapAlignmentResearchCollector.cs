using OpenCvSharp;
using System.Text.Json;
using System.Threading.Channels;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Independent, local-only research sink.  Collection failures are isolated
/// from recognition and reported only through the ordinary warning log.
/// </summary>
public sealed class MapAlignmentResearchCollector : IAsyncDisposable
{
    private sealed record WriteRequest(
        string Json,
        Guid AttemptId,
        IReadOnlyDictionary<string, byte[]> Artifacts);

    private readonly object _gate = new();
    private readonly HashSet<string> _representativeSuccesses = [];
    private Channel<WriteRequest>? _channel;
    private Task? _worker;
    private string? _sessionDirectory;
    private long _recordCount;
    private bool _disposed;
    private readonly TimeSpan _retention;
    private readonly long _maximumBytes;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string RootDirectory { get; }

    public MapAlignmentResearchCollector(
        string? rootDirectory = null,
        TimeSpan? retention = null,
        long maximumBytes = 2L * 1024L * 1024L * 1024L)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            global::IDVBuff.AppDataPaths.RootDirectory,
            "AlignmentResearch"));
        _retention = retention ?? TimeSpan.FromDays(30);
        _maximumBytes = Math.Max(1L, maximumBytes);
    }

    public bool IsEnabled => Volatile.Read(ref _channel) is not null;
    public long RecordCount => Interlocked.Read(ref _recordCount);
    public string? CurrentSessionDirectory => _sessionDirectory;

    public async Task SetEnabledAsync(bool enabled)
    {
        Channel<WriteRequest>? channelToClose = null;
        Task? workerToWait = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (enabled == (_channel is not null))
                return;
            if (enabled)
            {
                Directory.CreateDirectory(RootDirectory);
                _sessionDirectory = Path.Combine(
                    RootDirectory,
                    $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
                Directory.CreateDirectory(_sessionDirectory);
                _channel = Channel.CreateUnbounded<WriteRequest>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false
                    });
                _worker = RunWriterAsync(_channel.Reader, _sessionDirectory);
                _representativeSuccesses.Clear();
                Interlocked.Exchange(ref _recordCount, 0);
                CleanupSessions();
                return;
            }

            channelToClose = _channel;
            workerToWait = _worker;
            _channel = null;
            _worker = null;
        }

        channelToClose?.Writer.TryComplete();
        if (workerToWait is not null)
        {
            try
            {
                await workerToWait.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception)
            {
                Warn(exception);
            }
        }
    }

    public void Record(MapAlignmentResearchAttempt attempt, Mat? liveViewport = null)
    {
        var channel = Volatile.Read(ref _channel);
        if (channel is null)
            return;
        try
        {
            var artifacts = ShouldCaptureArtifacts(attempt)
                ? EncodeArtifacts(attempt, liveViewport)
                : new Dictionary<string, byte[]>();
            var request = new WriteRequest(
                JsonSerializer.Serialize(attempt, SerializerOptions),
                attempt.AttemptId,
                artifacts);
            if (channel.Writer.TryWrite(request))
                Interlocked.Increment(ref _recordCount);
        }
        catch (Exception exception)
        {
            Warn(exception);
        }
    }

    private bool ShouldCaptureArtifacts(MapAlignmentResearchAttempt attempt)
    {
        if (!attempt.Accepted || !string.IsNullOrWhiteSpace(attempt.CalibrationRejectionReason))
            return true;
        var tier = attempt.StableConfirmationRequiredFrames > 0
            && attempt.StableConfirmationFrames
                >= attempt.StableConfirmationRequiredFrames
            ? "medium-stable"
            : attempt.IsHighConfidence
                ? "high"
                : null;
        if (tier is null)
            return false;
        lock (_gate)
            return _representativeSuccesses.Add($"{attempt.MapId:N}:{attempt.FloorKey}:{tier}");
    }

    private static IReadOnlyDictionary<string, byte[]> EncodeArtifacts(
        MapAlignmentResearchAttempt attempt,
        Mat? liveViewport)
    {
        var result = new Dictionary<string, byte[]>();
        if (liveViewport is null || liveViewport.Empty())
            return result;
        Cv2.ImEncode(".png", liveViewport, out var liveBytes);
        result["live-viewport.png"] = liveBytes;
        using var edges = GateTemplateDetector.CreateEdges(liveViewport);
        Cv2.ImEncode(".png", edges, out var edgeBytes);
        result["structure-edges.png"] = edgeBytes;
        using var mask = new Mat(liveViewport.Size(), MatType.CV_8UC1, Scalar.Black);
        if (attempt.ValidMapBounds is { IsValid: true } bounds
            && attempt.FinalTransform is { } transform
            && attempt.WindowSignature is { } signature)
        {
            var left = (int)Math.Floor(
                bounds.X * transform.ScaleX + transform.OffsetX - signature.ViewportX);
            var top = (int)Math.Floor(
                bounds.Y * transform.ScaleY + transform.OffsetY - signature.ViewportY);
            var right = (int)Math.Ceiling(
                bounds.Right * transform.ScaleX + transform.OffsetX - signature.ViewportX);
            var bottom = (int)Math.Ceiling(
                bounds.Bottom * transform.ScaleY + transform.OffsetY - signature.ViewportY);
            left = Math.Clamp(left, 0, liveViewport.Width);
            top = Math.Clamp(top, 0, liveViewport.Height);
            right = Math.Clamp(right, left, liveViewport.Width);
            bottom = Math.Clamp(bottom, top, liveViewport.Height);
            if (right > left && bottom > top)
                Cv2.Rectangle(mask, new Rect(left, top, right - left, bottom - top), Scalar.White, -1);
        }
        else
        {
            mask.SetTo(Scalar.White);
        }
        Cv2.ImEncode(".png", mask, out var maskBytes);
        result["valid-mask.png"] = maskBytes;
        using var overlay = liveViewport.Clone();
        using (var maskPoints = new Mat())
        {
            Cv2.FindNonZero(mask, maskPoints);
            if (!maskPoints.Empty())
            {
                var projectedBounds = Cv2.BoundingRect(maskPoints);
                Cv2.Rectangle(overlay, projectedBounds, Scalar.LimeGreen, 2);
            }
        }
        var text = attempt.Accepted
            ? $"accepted {attempt.Confidence:P0}"
            : attempt.FailureCategory.ToString();
        Cv2.PutText(
            overlay,
            text,
            new Point(12, 28),
            HersheyFonts.HersheySimplex,
            0.7,
            Scalar.Red,
            2);
        Cv2.ImEncode(".png", overlay, out var overlayBytes);
        result["candidate-overlay.png"] = overlayBytes;
        return result;
    }

    private async Task RunWriterAsync(
        ChannelReader<WriteRequest> reader,
        string sessionDirectory)
    {
        var attemptsPath = Path.Combine(sessionDirectory, "attempts.jsonl");
        await using var stream = new FileStream(
            attemptsPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            32 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(stream);
        var written = 0;
        await foreach (var request in reader.ReadAllAsync())
        {
            try
            {
                await writer.WriteLineAsync(request.Json);
                await writer.FlushAsync();
                written++;
                if (written % 100 == 0)
                    CleanupSessions();
                if (request.Artifacts.Count == 0)
                    continue;
                var artifactDirectory = Path.Combine(
                    sessionDirectory,
                    "artifacts",
                    request.AttemptId.ToString("N"));
                Directory.CreateDirectory(artifactDirectory);
                foreach (var (name, bytes) in request.Artifacts)
                {
                    await File.WriteAllBytesAsync(
                        Path.Combine(artifactDirectory, name),
                        bytes);
                }
            }
            catch (Exception exception)
            {
                Warn(exception);
            }
        }
    }

    private void CleanupSessions()
    {
        try
        {
            var root = Path.GetFullPath(RootDirectory);
            var sessions = new DirectoryInfo(root)
                .EnumerateDirectories()
                .OrderBy(directory => directory.CreationTimeUtc)
                .ToList();
            var cutoff = DateTime.UtcNow - _retention;
            foreach (var expired in sessions
                .Where(directory => directory.CreationTimeUtc < cutoff)
                .ToArray())
            {
                if (!string.Equals(expired.FullName, _sessionDirectory, StringComparison.OrdinalIgnoreCase))
                    expired.Delete(recursive: true);
                sessions.Remove(expired);
            }

            long totalBytes = sessions.Sum(GetDirectorySize);
            foreach (var oldest in sessions)
            {
                if (totalBytes <= _maximumBytes)
                    break;
                if (string.Equals(oldest.FullName, _sessionDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;
                var bytes = GetDirectorySize(oldest);
                oldest.Delete(recursive: true);
                totalBytes -= bytes;
            }
        }
        catch (Exception exception)
        {
            Warn(exception);
        }
    }

    private static long GetDirectorySize(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(file => file.Length);
        }
        catch
        {
            return 0L;
        }
    }

    private static void Warn(Exception exception) =>
        MapLogCollector.Instance.Append(
            MapLogCategory.System,
            MapLogLevel.Warning,
            $"Alignment research collection failed: {exception.Message}");

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }
        if (IsEnabled)
            await SetEnabledAsync(false);
        lock (_gate)
            _disposed = true;
    }
}
