using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal static class SystemRelativeClock
{
    internal static long GetTicks() => (long)(Stopwatch.GetTimestamp()
        * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));

    internal static double AgeMilliseconds(long timestamp) => timestamp <= 0
        ? 0d
        : Math.Max(0d, (GetTicks() - timestamp) / (double)TimeSpan.TicksPerMillisecond);
}

/// <summary>
/// Fixed-capacity cursor history in the same 100ns system-relative time domain
/// used by WGC SystemRelativeTime.
/// </summary>
internal sealed class TimestampedMouseHistory
{
    private const int Capacity = 512;
    private readonly MouseSample[] _samples = new MouseSample[Capacity];
    private int _start;
    private int _count;
    private int _lastX;
    private int _lastY;
    private double _cumulativeX;
    private double _cumulativeY;
    private bool _initialized;

    internal void Reset(int x, int y, long timestamp)
    {
        _start = 0;
        _count = 0;
        _lastX = x;
        _lastY = y;
        _cumulativeX = 0d;
        _cumulativeY = 0d;
        _initialized = true;
        Add(new MouseSample(timestamp, 0d, 0d));
    }

    internal (double Dx, double Dy) Record(int x, int y, long timestamp, double scaleRatio)
    {
        if (!_initialized)
        {
            Reset(x, y, timestamp);
            return default;
        }
        var dx = (x - _lastX) * scaleRatio;
        var dy = (y - _lastY) * scaleRatio;
        _lastX = x;
        _lastY = y;
        _cumulativeX += dx;
        _cumulativeY += dy;
        Add(new MouseSample(timestamp, _cumulativeX, _cumulativeY));
        return (dx, dy);
    }

    internal (double Dx, double Dy) DeltaAfter(long timestamp)
    {
        if (_count == 0)
            return default;
        var atTimestamp = Interpolate(timestamp);
        return (_cumulativeX - atTimestamp.X, _cumulativeY - atTimestamp.Y);
    }

    private (double X, double Y) Interpolate(long timestamp)
    {
        var first = Get(0);
        if (timestamp <= first.Timestamp)
            return (first.CumulativeX, first.CumulativeY);
        var last = Get(_count - 1);
        if (timestamp >= last.Timestamp)
            return (last.CumulativeX, last.CumulativeY);

        for (var index = 1; index < _count; index++)
        {
            var right = Get(index);
            if (timestamp > right.Timestamp)
                continue;
            var left = Get(index - 1);
            var span = right.Timestamp - left.Timestamp;
            if (span <= 0)
                return (right.CumulativeX, right.CumulativeY);
            var amount = Math.Clamp((timestamp - left.Timestamp) / (double)span, 0d, 1d);
            return (
                left.CumulativeX + ((right.CumulativeX - left.CumulativeX) * amount),
                left.CumulativeY + ((right.CumulativeY - left.CumulativeY) * amount));
        }
        return (last.CumulativeX, last.CumulativeY);
    }

    private void Add(MouseSample sample)
    {
        if (_count < Capacity)
        {
            _samples[(_start + _count) % Capacity] = sample;
            _count++;
            return;
        }
        _samples[_start] = sample;
        _start = (_start + 1) % Capacity;
    }

    private MouseSample Get(int index) => _samples[(_start + index) % Capacity];

    private readonly record struct MouseSample(
        long Timestamp,
        double CumulativeX,
        double CumulativeY);
}

internal readonly record struct Vpsg3_5OpticalFlowResult(
    bool Accepted,
    double DeltaX,
    double DeltaY,
    int TrackedPoints,
    double InlierRatio,
    double PreprocessMilliseconds,
    double TrackMilliseconds,
    string RejectionReason);

/// <summary>Pyramidal LK frame-to-frame translation tracker.</summary>
internal sealed class Vpsg3_5OpticalFlowTracker : IDisposable
{
    private const int MaximumCorners = 240;
    private Mat? _previousGray;
    private Point2f[] _previousPoints = [];

    internal Vpsg3_5OpticalFlowResult Track(Mat image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var preprocessStarted = Stopwatch.GetTimestamp();
        using var gray = new Mat();
        if (image.Channels() == 1)
            image.CopyTo(gray);
        else
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0.8d);
        var preprocessMs = Stopwatch.GetElapsedTime(preprocessStarted).TotalMilliseconds;

        if (_previousGray is null || _previousPoints.Length < 12)
        {
            Reanchor(gray);
            return new Vpsg3_5OpticalFlowResult(
                false, 0d, 0d, _previousPoints.Length, 0d,
                preprocessMs, 0d, "OpticalFlowPrimed");
        }

        var trackStarted = Stopwatch.GetTimestamp();
        Point2f[] nextPoints = [];
        Cv2.CalcOpticalFlowPyrLK(
            _previousGray,
            gray,
            _previousPoints,
            ref nextPoints,
            out var status,
            out var errors,
            new Size(21, 21),
            3,
            new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 20, 0.03d),
            OpticalFlowFlags.None,
            0.0001d);

        var valid = new List<(Point2f Point, double Dx, double Dy)>(_previousPoints.Length);
        for (var index = 0; index < _previousPoints.Length; index++)
        {
            if (status[index] == 0 || errors[index] > 24f)
                continue;
            var next = nextPoints[index];
            if (next.X < 0 || next.Y < 0 || next.X >= gray.Width || next.Y >= gray.Height)
                continue;
            valid.Add((next, next.X - _previousPoints[index].X, next.Y - _previousPoints[index].Y));
        }

        if (valid.Count < 12)
        {
            Reanchor(gray);
            return new Vpsg3_5OpticalFlowResult(
                false, 0d, 0d, valid.Count, 0d,
                preprocessMs,
                Stopwatch.GetElapsedTime(trackStarted).TotalMilliseconds,
                "InsufficientTrackedPoints");
        }

        var medianX = Median(valid.Select(item => item.Dx));
        var medianY = Median(valid.Select(item => item.Dy));
        var residuals = valid
            .Select(item => Math.Sqrt(
                Math.Pow(item.Dx - medianX, 2d)
                + Math.Pow(item.Dy - medianY, 2d)))
            .ToArray();
        var medianResidual = Median(residuals);
        var threshold = Math.Max(1.25d, medianResidual * 2.5d);
        var inliers = valid
            .Where((_, index) => residuals[index] <= threshold)
            .ToArray();
        var inlierRatio = inliers.Length / (double)valid.Count;
        var deltaX = Median(inliers.Select(item => item.Dx));
        var deltaY = Median(inliers.Select(item => item.Dy));
        var magnitude = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));
        var accepted = inliers.Length >= 12
            && inlierRatio >= 0.55d
            && magnitude <= 160d;

        _previousGray.Dispose();
        _previousGray = gray.Clone();
        _previousPoints = inliers.Length >= 48
            ? inliers.Select(item => item.Point).ToArray()
            : DetectCorners(gray);
        return new Vpsg3_5OpticalFlowResult(
            accepted,
            accepted ? deltaX : 0d,
            accepted ? deltaY : 0d,
            inliers.Length,
            inlierRatio,
            preprocessMs,
            Stopwatch.GetElapsedTime(trackStarted).TotalMilliseconds,
            accepted ? string.Empty : "OpticalFlowConsensusRejected");
    }

    internal void Reset()
    {
        _previousGray?.Dispose();
        _previousGray = null;
        _previousPoints = [];
    }

    public void Dispose() => Reset();

    private void Reanchor(Mat gray)
    {
        _previousGray?.Dispose();
        _previousGray = gray.Clone();
        _previousPoints = DetectCorners(gray);
    }

    private static Point2f[] DetectCorners(Mat gray)
    {
        using var mask = new Mat();
        return Cv2.GoodFeaturesToTrack(
            gray,
            MaximumCorners,
            0.01d,
            8d,
            mask,
            3,
            false,
            0.04d);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
            return 0d;
        var middle = ordered.Length / 2;
        return (ordered.Length & 1) == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2d
            : ordered[middle];
    }
}
