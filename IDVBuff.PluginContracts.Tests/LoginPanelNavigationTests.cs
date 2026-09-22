using IDVBuff.Plugins.IdvLogin;
using Xunit;

namespace IDVBuff.PluginContracts.Tests;

public class LoginPanelNavigationTests
{
    [Fact]
    public void BackFromRestoredQrWalksThroughChannelsAndAccountsBeforeClosing()
    {
        var navigation = new LoginPanelNavigation();
        navigation.Navigate(LoginPanelPage.Import);
        Assert.True(navigation.Back());
        Assert.Equal(LoginPanelPage.Channels, navigation.Page);
        Assert.True(navigation.Back());
        Assert.Equal(LoginPanelPage.Accounts, navigation.Page);
        Assert.False(navigation.Back());
    }

    [Fact]
    public void LeavingAndReenteringSamePageRejectsOldAsyncResponse()
    {
        var navigation = new LoginPanelNavigation();
        var originalRequest = navigation.Revision;
        navigation.Navigate(LoginPanelPage.Channels);
        navigation.Back();
        Assert.Equal(LoginPanelPage.Accounts, navigation.Page);
        Assert.False(navigation.IsCurrent(originalRequest));
        Assert.True(navigation.IsCurrent(navigation.Revision));
    }

    [Fact]
    public async Task LateQrDecodeCannotReplaceChannelPageAfterBack()
    {
        var navigation = new LoginPanelNavigation();
        navigation.Navigate(LoginPanelPage.Import);
        var qrRevision = navigation.Revision;
        var decode = new TaskCompletionSource<string>();
        var content = "qr-loading";
        async Task ApplyQrAsync()
        {
            var image = await decode.Task;
            if (navigation.IsCurrent(qrRevision)) content = image;
        }
        var pending = ApplyQrAsync();
        navigation.Back();
        content = "channels";
        decode.SetResult("old-qr");
        await pending;
        Assert.Equal("channels", content);
    }
}
