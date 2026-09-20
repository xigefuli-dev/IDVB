using Microsoft.UI.Xaml;
using IDVBuff.Features.Announcements;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private void InitializeNotifications()
    {
        AnnouncementService.Instance.UnreadCountChanged += AnnouncementService_UnreadCountChanged;
        UpdateNotificationBadge(AnnouncementService.Instance.GetUnreadCount());
    }

    private void CleanupNotifications()
    {
        AnnouncementService.Instance.UnreadCountChanged -= AnnouncementService_UnreadCountChanged;
    }

    private void AnnouncementService_UnreadCountChanged()
    {
        DispatcherQueue.TryEnqueue(() => UpdateNotificationBadge(AnnouncementService.Instance.GetUnreadCount()));
    }

    private void UpdateNotificationBadge(int count)
    {
        if (NotificationBadge == null || NotificationBadgeText == null) return;

        if (count <= 0)
        {
            NotificationBadge.Visibility = Visibility.Collapsed;
        }
        else if (count <= 99)
        {
            NotificationBadge.Visibility = Visibility.Visible;
            NotificationBadgeText.Text = count.ToString();
        }
        else
        {
            NotificationBadge.Visibility = Visibility.Visible;
            NotificationBadgeText.Text = "…";
        }
    }

    private void NotificationButton_Click(object sender, RoutedEventArgs e)
    {
        AnnouncementWindow.Show();
    }
}
