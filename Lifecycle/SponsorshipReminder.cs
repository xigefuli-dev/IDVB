namespace IDVBuff.Lifecycle;

internal readonly record struct SponsorshipStartupContext(bool HomeReady, bool Foreground,
    bool Visible, bool InMatch, bool MapOpen, bool OtherPopup, bool Stopping)
{
    internal bool Suitable => HomeReady && Foreground && Visible && !InMatch
        && !MapOpen && !OtherPopup && !Stopping;
}

/// <summary>A single startup opportunity; the durable reservation favors no repeat disturbance.</summary>
internal sealed class SponsorshipReminder(TimeSpan? previousUsage, string markerPath)
{
    private int _attempted;
    internal bool TryReserve(bool suitable, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0 || !suitable
            || previousUsage is null || previousUsage < TimeSpan.FromHours(24)
            || cancellationToken.IsCancellationRequested) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            // CreateNew is atomic across processes. Keep even a partial/empty marker after
            // a crash: losing this optional prompt is preferable to repeating it.
            using var file = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            file.Write(System.Text.Encoding.UTF8.GetBytes("reserved-v1"));
            file.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[Sponsorship] Reminder suppressed: {exception.Message}");
            return false;
        }
    }
}
