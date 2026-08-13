using Microsoft.UI.Xaml;

namespace IDVBuff.Updater;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            args.Handled = true;
            UpdateLog.Write("Unhandled updater exception", args.Exception);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new UpdaterWindow(UpdaterLaunchOptions.Parse(Environment.GetCommandLineArgs()));
        _window.Activate();
    }
}
