using IDVBuff.Diagnostics;
using IDVBuff.Features.Maps;
using IDVBuff.Survey.Domain;
using IDVBuff.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using System.Numerics;
using IDVBuff.Lifecycle;

namespace IDVBuff;

public partial class App
{
    private bool _startupPresentationPending;
    private bool _startupTransitionComplete;
    private StartupWindowInteractionGuard? _startupWindowInteractionGuard;
    private StartupFocusSnapshot _startupLaunchFocus;
    private readonly CancellationTokenSource _startupPresentationCancellation = new();
    private readonly TaskCompletionSource _mainWindowPresentationCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes only after the startup splash transition has yielded a usable main window.
    /// Secondary windows must wait for this instead of appearing over an incomplete shell.
    /// </summary>
    internal static Task MainWindowPresentationCompleted =>
        _currentApp?._mainWindowPresentationCompleted.Task ?? Task.CompletedTask;

    private async Task CompleteStartupPresentationAsync(bool startMinimized)
    {
        if (IsApplicationStopping)
            return;
        var page = _mainFrame?.Content as MainPage;
        var visual = _mainFrame is null ? null
            : Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_mainFrame);

        if (!startMinimized
            && page is not null
            && !page.InitialReady.IsCompleted
            && window is not null)
        {
            // Loaded is not guaranteed while the HWND remains DWM-cloaked.
            // The splash is an independent topmost window, so present the
            // transparent shell behind it first; this lets WinUI attach and
            // load MainPage/HomePage before the visible handoff begins.
            if (visual is not null)
                visual.Opacity = 0f;
            SetMainWindowCloaked(false);
            ShowWindow(
                WinRT.Interop.WindowNative.GetWindowHandle(window),
                8); // SW_SHOWNA preserves the restored window size and maximized state.
            WriteStartupTrace(
                "Main window primed behind startup splash; awaiting initial page readiness.");
            WriteStartupWindowState("main-primed");
        }

        if (page is not null && !startMinimized)
            await page.InitialReady.WaitAsync(_startupPresentationCancellation.Token);
        if (IsApplicationStopping)
            return;
        if (_startupTransitionComplete)
        {
            if (!startMinimized) ShowMainWindow();
            return;
        }
        _startupTransitionComplete = true;
        StartupSplash.Complete(StartupSplash.Stage.Ready);
        StartupSplash.Report("准备就绪");
        if (!startMinimized && !IsApplicationStopping)
        {
            if (visual is not null && _mainFrame is not null)
            {
                await StartupSplash.PrepareTransitionAsync();
                if (IsApplicationStopping || window is null) { StartupSplash.Close(); return; }

                Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(_mainFrame, true);
                visual.StopAnimation("Opacity");
                visual.StopAnimation("Translation");
                visual.Opacity = 0f;
                _mainFrame.Translation = new Vector3(0f, 16f, 0f);

                try
                {
                    _startupPresentationPending = false;
                    ShowMainWindow(bringToForeground: false);

                    var compositor = visual.Compositor;
                    var ease = compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.22f, 1f),
                        new Vector2(0.36f, 1f));

                    using var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
                    opacityAnim.InsertKeyFrame(0f, 0f);
                    opacityAnim.InsertKeyFrame(1f, 1f, ease);
                    opacityAnim.Duration = TimeSpan.FromMilliseconds(400);

                    using var translationAnim = compositor.CreateVector3KeyFrameAnimation();
                    translationAnim.InsertKeyFrame(0f, new Vector3(0f, 16f, 0f));
                    translationAnim.InsertKeyFrame(1f, Vector3.Zero, ease);
                    translationAnim.Duration = TimeSpan.FromMilliseconds(400);

                    visual.StartAnimation("Opacity", opacityAnim);
                    visual.StartAnimation("Translation", translationAnim);

                    await Task.WhenAll(StartupSplash.FadeOutAsync(), Task.Delay(400));
                }
                finally
                {
                    visual.StopAnimation("Opacity");
                    visual.StopAnimation("Translation");
                    visual.Opacity = 1f;
                    _mainFrame.Translation = Vector3.Zero;
                }
            }
            else
            {
                await StartupSplash.PrepareTransitionAsync();
                if (IsApplicationStopping || window is null) { StartupSplash.Close(); return; }
                _startupPresentationPending = false;
                ShowMainWindow(bringToForeground: false);
                await Task.WhenAll(StartupSplash.FadeOutAsync(), Task.Delay(380));
            }
        }
        _startupPresentationPending = false;
        await StartupSplash.CloseAsync();
        ReleaseStartupWindowInteractionGuard();
        if (IsApplicationStopping)
            return;
        if (!startMinimized && !IsApplicationStopping)
        {
            // Normal startup must yield a visible, active main window even if
            // the user clicked another application while the splash was loading.
            // Only StartMinimized opts out of this completed presentation.
            TraceStartupMainWindowHandoff();
            ShowMainWindow();
        }
        WriteStartupWindowState("main-after-handoff");
        _mainWindowPresentationCompleted.TrySetResult();
        WriteStartupTrace("Startup presentation complete; main page ready.");
        StartupTimeline.StopSampling();
    }

    private void BeginStartupWindowInteractionGuard()
    {
        if (window is null)
            return;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var wasForeground = StartupFocusSnapshot.Capture().ForegroundWindow == handle;
        _startupWindowInteractionGuard = new StartupWindowInteractionGuard(handle);
        // A foreground consent/access dialog is a direct interaction with IDVB.
        // Rebase only that case; otherwise preserve the snapshot from splash Show.
        if (wasForeground)
            _startupLaunchFocus = StartupFocusSnapshot.Capture();
        WriteStartupWindowState("main-input-guard-installed");
    }

    private void ReleaseStartupWindowInteractionGuard()
    {
        if (_startupWindowInteractionGuard is not { } guard)
            return;
        _startupWindowInteractionGuard = null;
        try
        {
            guard.Dispose();
        }
        catch (Exception exception)
        {
            WriteStartupTrace("Unable to restore startup window interaction state.", exception);
        }
        WriteStartupWindowState("main-input-guard-released");
    }

    private void TraceStartupMainWindowHandoff()
    {
        var current = StartupFocusSnapshot.Capture();
        WriteStartupTrace("Startup foreground handoff: requested=True; "
            + $"launchForeground=0x{_startupLaunchFocus.ForegroundWindow.ToInt64():X}; "
            + $"currentForeground=0x{current.ForegroundWindow.ToInt64():X}; "
            + $"launchInput={_startupLaunchFocus.LastInputTick}; currentInput={current.LastInputTick}.");
        WriteStartupWindowState("main-before-handoff");
    }

    private void WriteStartupWindowState(string stage)
    {
        if (window is not null)
            WriteStartupTrace(StartupWindowDiagnostics.Describe(stage,
                WinRT.Interop.WindowNative.GetWindowHandle(window)));
    }

    private void StopStartupPresentation()
    {
        _servicesReadyTcs.TrySetCanceled();
        _startupPresentationCancellation.Cancel();
        _mainWindowPresentationCompleted.TrySetCanceled();
        ReleaseStartupWindowInteractionGuard();
        StartupSplash.Close();
    }

    private FrameworkElement? _startupPlaceholder;
    private Frame? _mainFrame;
    private Grid? _startupHost;
    private MapListStartupData? _mapListStartupData;

    internal static MapListStartupData? TakeMapListStartupData()
    {
        var current = _currentApp;
        var data = current?._mapListStartupData;
        if (current is not null)
            current._mapListStartupData = null;
        return data;
    }

    private async Task PrepareMapListAsync(SessionOrchestrator? session)
    {
        try
        {
            WriteStartupTrace("Map catalog preload begin.");
            var repository = new MapRepository();
            var catalog = await repository.GetCatalogSnapshotAsync();
            if (IsApplicationStopping) return;
            WriteStartupTrace("Map catalog loaded; tag filters begin.");
            var filters = (await new MapTagStore().LoadAsync(catalog.Maps, catalog.Classes))
                .Where(group => group.IsEnabled)
                .ToArray();
            if (IsApplicationStopping) return;
            WriteStartupTrace("Tag filters loaded; survey projects begin.");
            IReadOnlyList<SurveyProjectSummary> projects = session is null
                ? []
                : await session.GetSurveyProjectsAsync();
            if (IsApplicationStopping) return;
            WriteStartupTrace("Survey projects loaded; catalog data prepared.");
            var previews = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
            _mapListStartupData = new MapListStartupData(
                repository.GetCatalogRevision(), catalog, filters, projects, previews);
            WriteStartupTrace($"Map list prepared: {catalog.Maps.Count} maps.");
            StartupSplash.Complete(StartupSplash.Stage.Catalog);
        }
        catch (Exception exception)
        {
            _mapListStartupData = null;
            WriteStartupTrace("Map list preloading failed; the page will retry when opened.", exception);
            // The optional preload step has finished via its existing page-level retry path.
            StartupSplash.Complete(StartupSplash.Stage.Catalog);
        }
    }

    private static async Task<IReadOnlyDictionary<string, BitmapImage>> PrepareMapPreviewsAsync(
        MapRepository repository,
        IReadOnlyList<MapRecord> maps)
    {
        var previews = new Dictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in maps)
        {
            var floorKey = MapScanFloorRules.ResolveScanFloorKey(map);
            if (MapFloorRules.GetOrderedFloors(map).All(floor => floor.Key != floorKey))
                continue;
            var path = repository.GetFloorThumbnailPath(map, floorKey);
            if (!File.Exists(path))
                path = repository.GetFloorRecognitionPath(map, floorKey);
            if (!File.Exists(path) || previews.ContainsKey(path))
                continue;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = 400 };
                await bitmap.SetSourceAsync(stream);
                previews.Add(path, bitmap);
            }
            catch (Exception exception)
            {
                OutputLog.Write("WARN", "STARTUP", $"Map preview preloading failed: {path}", exception);
            }
        }
        return previews;
    }

    private Grid ShowStartupPlaceholder(Frame rootFrame)
    {
        _startupPlaceholder = new Grid { Background = FluentTheme.Brush(rootFrame, IDVBuff.Appearance.ThemeToken.Window) };
        _mainFrame = rootFrame;
        _startupHost = new Grid();
        _startupHost.Children.Add(rootFrame);
        _startupHost.Children.Add(_startupPlaceholder);
        _startupPlaceholder.Loaded += StartupPlaceholder_Loaded;
        WriteStartupTrace("Startup placeholder constructed (not yet attached or rendered).");
        return _startupHost;
    }

    private bool _observingStartupRendering;

    private void StartupPlaceholder_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.Loaded -= StartupPlaceholder_Loaded;
        WriteStartupTrace("Startup placeholder Loaded (visual tree attached; not proof of presentation).");
        _observingStartupRendering = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += StartupPlaceholder_Rendering;
    }

    private void StartupPlaceholder_Rendering(object? sender, object e)
    {
        StopStartupRenderObservation();
        WriteStartupTrace("First Rendering callback after placeholder Loaded (before composition; not display confirmation).");
    }

    private void StopStartupRenderObservation()
    {
        if (_startupPlaceholder is { } placeholder)
            placeholder.Loaded -= StartupPlaceholder_Loaded;
        if (!_observingStartupRendering)
            return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= StartupPlaceholder_Rendering;
        _observingStartupRendering = false;
    }

    private async Task ShowReadyMainPageAsync(Frame rootFrame, string arguments)
    {
        WriteStartupTrace("Main page navigation begin.");
        if (!rootFrame.Navigate(typeof(Views.MainPage), arguments))
            throw new InvalidOperationException("主界面导航失败。");
        if (rootFrame.Content is not Views.MainPage mainPage)
            throw new InvalidOperationException("主界面未能创建。");
        WriteStartupTrace("Main page constructed; waiting for InitialReady.");
        await mainPage.InitialReady;
        WriteStartupTrace("Main page InitialReady complete; removing startup placeholder.");
        StopStartupRenderObservation();
        if (_startupHost is { } host && _startupPlaceholder is { } placeholder)
            host.Children.Remove(placeholder);
        _startupPlaceholder = null;
        WriteStartupTrace("Main page ready.");
    }

    private static readonly string StartupLogPath = Path.Combine(
        AppDataPaths.RootDirectory, "Logs",
        $"startup-main-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");

    private static void WriteStartupTrace(string message, Exception? exception = null)
    {
        var detail = StartupTimeline.Write(message, exception);
        try
        {
            var logDirectory = Path.Combine(AppDataPaths.RootDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);
            var text = $"{DateTimeOffset.Now:O} {detail}";
            if (exception is not null)
                text += Environment.NewLine + exception;
            File.AppendAllText(StartupLogPath, text + Environment.NewLine,
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // Startup diagnostics must never make startup fail.
        }
    }
}
