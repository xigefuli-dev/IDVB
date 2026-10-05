namespace IDVBuff.Views;

public sealed partial class MainPage
{
    internal bool IsSponsorshipHome => ModuleContentHost.Content is HomePage && InitialReady.IsCompleted;
    private GentleSponsorshipMotion? _supportMotion;
    private GentleSponsorshipMotion SupportMotion => _supportMotion ??= new(SupportHeart);
    internal void OpenSponsorshipFromHome() => OpenSponsorship("home");
    private string _sponsorshipReturnModule = "main-settings";

    private bool TryOpenSponsorship(IDVBuff.Modules.NavigationEntry entry)
    {
        if (entry.ModuleId != "sponsorship") return false;
        // This control owns its animation policy, including reduced-motion settings.
        SupportMotion.Pulse();
        OpenSponsorship(_selectedNavigationEntry?.ModuleId ?? "home");
        return true;
    }

    private void ConnectSponsorshipNavigation(object view)
    {
        if (view is MainSettingsPage mainSettings)
            mainSettings.SponsorshipRequested += (_, _) => OpenSponsorship("main-settings");
        if (view is SettingsPage about)
            about.SponsorshipRequested += (_, _) => OpenSponsorship("settings");
        if (view is SponsorshipPage sponsorship)
            sponsorship.BackRequested += (_, _) => NavigateTo(_sponsorshipReturnModule);
    }

    private void OpenSponsorship(string returnModule)
    {
        _sponsorshipReturnModule = returnModule;
        NavigateTo("sponsorship");
    }
}
