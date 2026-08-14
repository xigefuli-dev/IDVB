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
        var options = UpdaterLaunchOptions.Parse(Environment.GetCommandLineArgs());
        _window = new UpdaterWindow(options);
        if (!options.Background)
            _window.Activate();
    }
}
