using IDVBuff.Appearance;
using IDVBuff.Features.Maps;
using System.Runtime.ExceptionServices;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IDVBuff.WinUI.RegressionTests;

public sealed partial class RegressionApp : Application
{
    private readonly RegressionTestRunner _runner;
    private Window? _host;
    private PanelFixture? _activeFixture;
    private bool _finishing;

    internal RegressionApp(RegressionTestRunner runner)
    {
        _runner = runner;
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            _runner.RecordHostFailure(args.Exception);
            args.Handled = true;
            _runner.Complete();
            Environment.ExitCode = 1;
            _finishing = true;
            _activeFixture?.Dispose();
            _host?.Close();
            Exit();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            ThemeService.Initialize(new() { Mode = AppearanceMode.Light, AccentSource = AccentSource.ThemeDefault },
                DispatcherQueue.GetForCurrentThread());
            _host = new Window { Title = "Identity Vision Bridge native regression tests", Content = new Grid() };
            _host.Closed += (_, _) =>
            {
                if (_finishing) return;
                _finishing = true;
                _runner.RecordHostFailure(new OperationCanceledException("The regression host was closed before its suite finished."));
                _activeFixture?.Dispose();
                _runner.Complete();
                Environment.ExitCode = 1;
                Exit();
            };
            _host.AppWindow.Resize(new Windows.Graphics.SizeInt32(120, 100));
            GameInputPreservingWindow.Apply(WinRT.Interop.WindowNative.GetWindowHandle(_host));
            _host.AppWindow.Show(false);
            foreach (var test in MapControlPanelThemeTests.Cases.Concat(NativeStartupWindowTests.Cases))
            {
                if (!await _runner.RunAsync(test.Name, async () =>
                {
                    var fixture = new PanelFixture(_host);
                    _activeFixture = fixture;
                    Exception? failure = null;
                    try { await test.Run(fixture); }
                    catch (Exception exception) { failure = exception; }
                    try { await fixture.DisposeAsync(); }
                    catch (Exception exception)
                    {
                        failure = failure is null ? exception
                            : new AggregateException("Regression case and fixture cleanup both failed.", failure, exception);
                    }
                    finally { _activeFixture = null; }
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                })) break;
            }
        }
        catch (Exception exception) { _runner.RecordHostFailure(exception); }
        finally
        {
            _finishing = true;
            _activeFixture?.Dispose();
            _host?.Close();
            _runner.Complete();
            Environment.ExitCode = _runner.HasFailures ? 1 : 0;
            Exit();
        }
    }
}
