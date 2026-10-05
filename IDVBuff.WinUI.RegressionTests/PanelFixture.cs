using System.Reflection;
using IDVBuff.Appearance;
using IDVBuff.Features.Maps;
using IDVBuff.Presentation.Theming;
using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xunit;

namespace IDVBuff.WinUI.RegressionTests;

internal sealed class PanelFixture : IDisposable, IAsyncDisposable
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly Window _host;
    private readonly List<ContentDialog> _dialogs = [];
    private readonly List<Task> _dialogTasks = [];
    private readonly List<Exception> _cleanupFailures = [];
    private bool _disposed;
    private string _remembered = "Healthy";
    public MapControlPanelWindow Panel { get; }
    public int Saves { get; private set; }
    public TaskCompletionSource? BeginWait { get; set; }
    public TaskCompletionSource? SaveWait { get; set; }
    public MapMatchSnapshot Ended { get; } = new(MapMatchState.Ended, null, 1);
    public MapMatchSnapshot Started { get; } = new(MapMatchState.Started, null, 2, "Healthy");
    public ComboBox Combo => Field<ComboBox>("_classComboBox");
    public Button BeginButton => Field<Button>("_beginButton");
    public Window Window => Field<Window>("_window");
    public Border Root => Assert.IsType<Border>(Window.Content);
    public ThemeScope Scope => Field<ThemeScope>("_themeScope");
    public Window Host => _host;

    public PanelFixture(Window host)
    {
        _host = host;
        ApplyMode(AppearanceMode.Light);
        PublishDiagnostics();
        var variants = new MapVariantSelectionContext(Guid.Parse("11111111-1111-1111-1111-111111111111"), [
            new(Guid.Parse("22222222-2222-2222-2222-222222222222"), 1, 1, "当前变体", true, false),
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), 2, 2, "候选变体", false, true)]);
        Panel = new MapControlPanelWindow(
            _ => BeginWait?.Task ?? Task.CompletedTask,
            () => Task.FromResult<IReadOnlyList<string>>(["Healthy", "Warning"]),
            () => _remembered,
            mapClass => { _remembered = mapClass; Saves++; return Task.CompletedTask; },
            () => true, _ => Task.CompletedTask, () => SurveyStatusSnapshot.Inactive, () => true,
            getVariantContext: () => Task.FromResult<MapVariantSelectionContext?>(variants),
            switchVariant: _ => Task.CompletedTask, correctMap: () => Task.CompletedTask);
    }

    public static void ApplyMode(AppearanceMode mode) => ThemeService.Apply(new()
    {
        Mode = mode, AccentSource = AccentSource.ThemeDefault, AccentFollowsScanMode = false, Material = ThemeMaterial.Solid
    });

    public static void PublishDiagnostics(bool firstHealthy = true) => MapClassDiagnosticCoordinator.Instance.Publish(
        new("Healthy", firstHealthy, firstHealthy ? [] : ["缺少结构线图"]),
        new("Warning", false, ["缺少结构线图"]));

    public async Task ShowAsync(MapMatchSnapshot? snapshot = null)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        Combo.Loaded += OnLoaded;
        try
        {
            if (Combo.IsLoaded) loaded.TrySetResult();
            await Panel.ShowAsync(new(20, 20, 1000, 780), WindowNative.GetWindowHandle(_host), snapshot ?? Ended)
                .WaitAsync(NativeThemeAssertions.WaitTimeout);
            await loaded.Task.WaitAsync(NativeThemeAssertions.WaitTimeout);
            Assert.True(Panel.IsVisible);
            await NativeThemeAssertions.RenderAsync(Root);
        }
        finally { Combo.Loaded -= OnLoaded; }
    }

    public T Field<T>(string name)
    {
        var field = typeof(MapControlPanelWindow).GetField(name, Private);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field.GetValue(Panel));
    }

    public object? Invoke(string name, params object[] args)
    {
        var method = typeof(MapControlPanelWindow).GetMethod(name, Private);
        Assert.NotNull(method);
        return method.Invoke(Panel, args);
    }

    public void BeginWhileSaving()
    {
        BeginWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SaveWait = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MapControlPanelWindow).GetField("_lastMapClassSaveTask", Private)!.SetValue(Panel, SaveWait.Task);
        Invoke("BeginButton_Click", BeginButton, new RoutedEventArgs());
    }

    public ContentDialog[] OpenDialogs() => VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
        .SelectMany(popup => NativeThemeAssertions.Descendants(popup.Child)).OfType<ContentDialog>().ToArray();

    public void Track(ContentDialog dialog) => _dialogs.Add(dialog);
    public void TrackDialogTask(Task task) => _dialogTasks.Add(task);

    public void DismissOwnedDialogs()
    {
        var dialogs = new List<ContentDialog>(_dialogs);
        try
        {
            var window = typeof(MapControlPanelWindow).GetField("_window", Private)!.GetValue(Panel) as Window;
            if ((window?.Content as FrameworkElement)?.XamlRoot is { } root)
                dialogs.AddRange(VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
                    .SelectMany(popup => NativeThemeAssertions.Descendants(popup.Child)).OfType<ContentDialog>());
        }
        catch (Exception exception) { _cleanupFailures.Add(exception); }
        foreach (var dialog in dialogs.Distinct())
        {
            try { dialog.Hide(); }
            catch (Exception exception) { _cleanupFailures.Add(exception); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveWait?.TrySetResult();
        BeginWait?.TrySetResult();
        DismissOwnedDialogs();
        try { Combo.IsDropDownOpen = false; }
        catch (Exception exception) { _cleanupFailures.Add(exception); }
        try { Panel.Dispose(); }
        catch (Exception exception) { _cleanupFailures.Add(exception); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        SaveWait?.TrySetResult();
        BeginWait?.TrySetResult();
        DismissOwnedDialogs();
        try { await Task.WhenAll(_dialogTasks).WaitAsync(NativeThemeAssertions.WaitTimeout); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _cleanupFailures.Add(exception); }
        try { await NativeThemeAssertions.DispatcherTurnAsync(); }
        catch (Exception exception) { _cleanupFailures.Add(exception); }
        Dispose();
        if (_cleanupFailures.Count > 0)
            throw new NativeFixtureCleanupException(_cleanupFailures);
    }
}

internal sealed class NativeFixtureCleanupException : AggregateException
{
    public NativeFixtureCleanupException(IEnumerable<Exception> failures)
        : base("Native fixture cleanup failed.", failures) { }
}
