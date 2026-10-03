using System.Drawing;
using System.Drawing.Imaging;
using IDVBuff.Core.Contracts;
using IDVBuff.Features.Notifications;
using Xunit;

namespace IDVBuff.Tests;

public sealed partial class OverlayNotificationTests
{
    [Fact]
    public void CustomDurationStartsAtActualDisplayAndPreservesCompletionAndInfiniteLifetimes()
    {
        var item = new OverlayNotificationItem(OverlayNotificationType.Notice, "plugin", duration: TimeSpan.FromSeconds(8));
        var displayedAt = item.CreatedAt.AddMinutes(1);
        item.StartDisplayLifetime(displayedAt);
        Assert.Equal(displayedAt.AddSeconds(8), item.ExpireTime);
        item.UpdateProgress(1);
        item.StartDisplayLifetime(displayedAt);
        Assert.Equal(displayedAt.AddSeconds(1.5), item.ExpireTime);
        var persistent = new OverlayNotificationItem(OverlayNotificationType.Notice, "progress", duration: Timeout.InfiniteTimeSpan);
        persistent.StartDisplayLifetime(displayedAt);
        Assert.Null(persistent.ExpireTime);
    }

    [Fact]
    public void QueuedNotificationGetsItsFullDurationWhenPromoted()
    {
        var queue = new OverlayNotificationQueue();
        var active = Enumerable.Range(0, 5).Select(index => new OverlayNotificationItem(
            OverlayNotificationType.Notice, $"active {index}", duration: Timeout.InfiniteTimeSpan)).ToArray();
        foreach (var item in active) queue.Enqueue(item);
        var pending = new OverlayNotificationItem(OverlayNotificationType.Notice, "pending", duration: TimeSpan.FromSeconds(8));
        pending.StartDisplayLifetime(pending.CreatedAt.AddMinutes(-1));
        queue.Enqueue(pending);
        active[0].Dismiss();
        using var bitmap = new Bitmap(1, 1, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var now = Environment.TickCount64;
        queue.UpdateFrame(graphics, now);
        queue.UpdateFrame(graphics, now + 1000);
        Assert.True(pending.ExpireTime > DateTimeOffset.UtcNow);
        queue.UpdateFrame(graphics, now + 1001);
        Assert.False(Assert.Single(queue.GetSnapshot(), card => card.Notification.Id == pending.Id).IsExiting);
    }
}
