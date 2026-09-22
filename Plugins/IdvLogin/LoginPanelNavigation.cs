namespace IDVBuff.Plugins.IdvLogin;

public enum LoginPanelPage { Accounts, Channels, Import }

/// <summary>Navigation is independent of the external login task's lifecycle.</summary>
public sealed class LoginPanelNavigation
{
    public LoginPanelPage Page { get; private set; } = LoginPanelPage.Accounts;
    public int Revision { get; private set; }

    public void Navigate(LoginPanelPage page)
    {
        Page = page;
        Revision++;
    }

    public bool Back()
    {
        if (Page == LoginPanelPage.Accounts) return false;
        Navigate(Page == LoginPanelPage.Import ? LoginPanelPage.Channels : LoginPanelPage.Accounts);
        return true;
    }

    public bool IsCurrent(int revision) => revision == Revision;
}
