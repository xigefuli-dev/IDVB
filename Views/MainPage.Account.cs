using IDVBuff.Features.Accounts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IDVBuff.Views;

public sealed partial class MainPage
{
    private void AccountSession_Changed(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(UpdateAccountNavigation);

    private void UpdateAccountNavigation()
    {
        var identity = AccountSession.Identity;
        var name = identity?.DisplayName.TrimStart('@') ?? "未登录";
        AccountNavigationLabel.Text = name;
        AccountNavigationAvatar.DisplayName = name;
        AccountNavigationAvatar.ProfilePicture = Uri.TryCreate(identity?.AvatarUrl, UriKind.Absolute, out var avatar)
            ? new BitmapImage(avatar)
            : null;
        AccountOfficialBadge.Visibility = identity?.IsOfficial == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolTipService.SetToolTip(AccountNavigationButton, name);
    }
}
