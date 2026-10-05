using System.Runtime.InteropServices;
using System.Reflection;
using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xunit;
using static IDVBuff.WinUI.RegressionTests.NativeThemeAssertions;

namespace IDVBuff.WinUI.RegressionTests;

internal static class MapControlPanelThemeTests
{
    public static IReadOnlyList<NativeRegressionCase> Cases { get; } =
    [
        new("Theme/LightDarkSwitchUpdatesExistingBrushes", LightDarkSwitchUpdatesExistingBrushes),
        new("Theme/LightNativeStatesAndDiagnostics", fixture => NativeStatesAndDiagnostics(fixture, AppearanceMode.Light)),
        new("Theme/DarkNativeStatesAndDiagnostics", fixture => NativeStatesAndDiagnostics(fixture, AppearanceMode.Dark)),
        new("Theme/SystemNativeStatesAndDiagnostics", fixture => NativeStatesAndDiagnostics(fixture, AppearanceMode.System)),
        new("Theme/DiagnosticRefreshPreservesNativeBrushes", DiagnosticRefreshPreservesNativeBrushes),
        new("Theme/OpenPopupFollowsOwnerScope", OpenPopupFollowsOwnerScope),
        new("Theme/HideAndCloseRecreationRetainsControlOwnership", HideAndCloseRecreationRetainsControlOwnership),
        new("Theme/CacheSaveDialogTracksLiveTheme", CacheSaveDialogTracksLiveTheme),
        new("Theme/CancellationClosesNativeDialogBeforeDrain", CancellationClosesNativeDialogBeforeDrain),
        new("Theme/AsyncBeginKeepsDisabledPaletteUntilReenabled", AsyncBeginKeepsDisabledPaletteUntilReenabled),
        new("Theme/SelectedVariantSurvivesAccentAndHighContrast", SelectedVariantSurvivesAccentAndHighContrast),
        new("Theme/DisposeDetachesActualOwnerSubscription", DisposeDetachesActualOwnerSubscription)
    ];

    private static async Task LightDarkSwitchUpdatesExistingBrushes(PanelFixture fixture)
    {
        var owner = ThemeService.For(fixture.Combo);
        var comboBrushes = NativeBrushes(fixture.Combo, "ComboBox");
        var buttonBrushes = NativeBrushes(fixture.BeginButton, "Button");
        await fixture.ShowAsync();
        var rootBrush = fixture.Root.Background;
        foreach (var mode in new[] { AppearanceMode.Light, AppearanceMode.Dark, AppearanceMode.Light })
        {
            PanelFixture.ApplyMode(mode);
            await RenderAsync(fixture.Root);
            Assert.Same(owner, ThemeService.For(fixture.Combo));
            Assert.Same(rootBrush, fixture.Root.Background);
            SameBrushes(fixture.Combo, comboBrushes);
            SameBrushes(fixture.BeginButton, buttonBrushes);
            Assert.Equal(mode == AppearanceMode.Dark, owner.Snapshot.IsDark);
            PanelPalette(fixture);
            await ButtonStatesAsync(fixture, fixture.BeginButton);
        }
    }

    private static async Task NativeStatesAndDiagnostics(PanelFixture fixture, AppearanceMode mode)
    {
        PanelFixture.ApplyMode(mode);
        await fixture.ShowAsync();
        PanelPalette(fixture);
        await ComboStatesAsync(fixture, warning: false);
        await ButtonStatesAsync(fixture, fixture.BeginButton);
        fixture.Combo.SelectedIndex = 1;
        await DispatcherTurnAsync();
        await ComboStatesAsync(fixture, warning: true);
        await WarningIconInOpenPopupAsync(fixture);
    }

    private static async Task WarningIconInOpenPopupAsync(PanelFixture fixture)
    {
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, object args) => opened.TrySetResult();
        fixture.Combo.DropDownOpened += OnOpened;
        try
        {
            // ComboBoxItem.Content is a logical Grid until the dropdown realizes
            // its row. Inspect the loaded popup rather than an unattached subtree.
            fixture.Combo.IsDropDownOpen = true;
            await opened.Task.WaitAsync(WaitTimeout);
            await RenderAsync(fixture.Root);
            var warning = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(fixture.Root.XamlRoot)
                .SelectMany(popup => Descendants(popup.Child)).OfType<FontIcon>()
                .Where(icon => icon.Glyph == "\uE7BA"));
            Assert.True(warning.IsLoaded);
            Assert.Same(fixture.Root.XamlRoot, warning.XamlRoot);
            Color(warning.Foreground, ThemeService.For(warning).Snapshot[ThemeToken.WarningText], "loaded diagnostic warning icon");
        }
        finally
        {
            fixture.Combo.IsDropDownOpen = false;
            fixture.Combo.DropDownOpened -= OnOpened;
        }
    }

    private static async Task DiagnosticRefreshPreservesNativeBrushes(PanelFixture fixture)
    {
        await fixture.ShowAsync();
        var brushes = NativeBrushes(fixture.Combo, "ComboBox");
        PanelFixture.PublishDiagnostics(firstHealthy: false);
        await DispatcherTurnAsync();
        await ComboStatesAsync(fixture, warning: true);
        SameBrushes(fixture.Combo, brushes);
        PanelFixture.PublishDiagnostics();
        await DispatcherTurnAsync();
        await ComboStatesAsync(fixture, warning: false);
        SameBrushes(fixture.Combo, brushes);
    }

    private static async Task OpenPopupFollowsOwnerScope(PanelFixture fixture)
    {
        await fixture.ShowAsync();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOpened(object? sender, object args) => opened.TrySetResult();
        fixture.Combo.DropDownOpened += OnOpened;
        try
        {
            fixture.Combo.IsDropDownOpen = true;
            await opened.Task.WaitAsync(WaitTimeout);
            await RenderAsync(fixture.Root);
            var popup = Assert.Single(VisualTreeHelper.GetOpenPopupsForXamlRoot(fixture.Root.XamlRoot)
                .Where(candidate => Descendants(candidate.Child).OfType<Border>().Any(border => border.Name == "PopupBorder")));
            var border = Assert.Single(Descendants(popup.Child).OfType<Border>().Where(candidate => candidate.Name == "PopupBorder"));
            Color(border.Background, ThemeService.For(fixture.Combo).Snapshot[ThemeToken.Flyout], "open popup light surface");
            PanelFixture.ApplyMode(AppearanceMode.Dark);
            await RenderAsync(fixture.Root);
            Color(border.Background, ThemeService.For(fixture.Combo).Snapshot[ThemeToken.Flyout], "already open popup changes to dark");
            PanelFixture.ApplyMode(AppearanceMode.Light);
            // A forced owner region differs from the application's Light preference.
            // This catches popups accidentally resolving only the global dictionary.
            using var ownerScope = ThemeService.AttachRegion(fixture.Combo, ThemeProfile.EditorDark);
            await RenderAsync(fixture.Root);
            Assert.True(ThemeService.For(fixture.Combo).Snapshot.IsDark);
            Assert.False(ThemeService.ApplicationResources.Snapshot.IsDark);
            Color(border.Background, ThemeService.For(fixture.Combo).Snapshot[ThemeToken.Flyout], "popup belongs to forced owner region");
        }
        finally
        {
            fixture.Combo.IsDropDownOpen = false;
            fixture.Combo.DropDownOpened -= OnOpened;
        }
    }

    private static async Task HideAndCloseRecreationRetainsControlOwnership(PanelFixture fixture)
    {
        await fixture.ShowAsync();
        var combo = fixture.Combo;
        var owner = ThemeService.For(combo);
        var content = fixture.Field<UIElement>("_content");
        var originalWindow = fixture.Window;
        var originalScope = fixture.Scope;
        fixture.Panel.Hide();
        Assert.False(fixture.Panel.IsVisible);
        PanelFixture.ApplyMode(AppearanceMode.Dark);
        await fixture.ShowAsync();
        PanelPalette(fixture);
        originalWindow.Close();
        await DispatcherTurnAsync();
        Assert.True(originalScope.IsDisposed);
        PanelFixture.ApplyMode(AppearanceMode.Light);
        await fixture.ShowAsync();
        Assert.NotSame(originalWindow, fixture.Window);
        Assert.Same(content, fixture.Field<UIElement>("_content"));
        Assert.Same(combo, fixture.Combo);
        Assert.Same(owner, ThemeService.For(combo));
        var saves = fixture.Saves;
        combo.SelectedIndex = 1;
        await DispatcherTurnAsync();
        Assert.Equal(saves + 1, fixture.Saves);
        await ComboStatesAsync(fixture, warning: true);
        var handle = WindowNative.GetWindowHandle(fixture.Window);
        Assert.NotEqual(0, GetWindowLong(handle, -20) & 0x08000000);
        Assert.Equal(new IntPtr(3), SendMessage(handle, 0x0021, IntPtr.Zero, IntPtr.Zero));
    }

    private static async Task CacheSaveDialogTracksLiveTheme(PanelFixture fixture)
    {
        await fixture.ShowAsync(fixture.Started);
        var shown = Assert.IsAssignableFrom<Task<bool>>(fixture.Invoke("ConfirmAutomaticMapCacheSaveAsync"));
        fixture.TrackDialogTask(shown);
        ContentDialog? dialog = null;
        try
        {
            await RenderAsync(fixture.Root);
            dialog = Assert.Single(fixture.OpenDialogs());
            fixture.Track(dialog);
            Color(dialog.Background, ThemeService.For(dialog).Snapshot[ThemeToken.Dialog], "production cache-save dialog surface");
            PanelFixture.ApplyMode(AppearanceMode.Dark);
            await RenderAsync(fixture.Root);
            Assert.True(ThemeService.For(dialog).Snapshot.IsDark);
            Color(dialog.Background, ThemeService.For(dialog).Snapshot[ThemeToken.Dialog], "already open cache-save dialog theme");
            dialog.Hide();
            Assert.False(await shown.WaitAsync(WaitTimeout));
        }
        finally
        {
            dialog?.Hide();
        }
    }

    private static async Task CancellationClosesNativeDialogBeforeDrain(PanelFixture fixture)
    {
        await fixture.ShowAsync();
        using var cancellation = new CancellationTokenSource();
        var dialog = new ContentDialog { XamlRoot = fixture.Root.XamlRoot, Title = "Cancellation regression", CloseButtonText = "Close" };
        fixture.Track(dialog);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        var shown = dialog.ShowThemedAsync(fixture.Root, cancellation.Token);
        fixture.TrackDialogTask(shown);
        try
        {
            await opened.Task.WaitAsync(WaitTimeout);
            Assert.Contains(dialog, fixture.OpenDialogs());
            // Run the cancellation callback on a background thread, as shutdown does.
            await Task.Run(cancellation.Cancel).WaitAsync(WaitTimeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await shown.WaitAsync(WaitTimeout));
            Assert.DoesNotContain(dialog, fixture.OpenDialogs());
        }
        finally
        {
            dialog.Hide();
        }
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        var neverOpened = new ContentDialog { XamlRoot = fixture.Root.XamlRoot, CloseButtonText = "Close" };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => neverOpened.ShowThemedAsync(fixture.Root, alreadyCancelled.Token));
        Assert.DoesNotContain(neverOpened, fixture.OpenDialogs());
    }

    private static async Task AsyncBeginKeepsDisabledPaletteUntilReenabled(PanelFixture fixture)
    {
        await fixture.ShowAsync();
        fixture.BeginWhileSaving();
        Assert.True(fixture.Panel.IsVisible);
        Assert.False(fixture.BeginButton.IsEnabled);
        await ButtonStatesAsync(fixture, fixture.BeginButton, onlyDisabled: true);
        PanelFixture.ApplyMode(AppearanceMode.Dark);
        await ButtonStatesAsync(fixture, fixture.BeginButton, onlyDisabled: true);
        fixture.SaveWait!.SetResult();
        await DispatcherTurnAsync();
        Assert.False(fixture.Panel.IsVisible);
        PanelFixture.ApplyMode(AppearanceMode.Light);
        Color(Assert.IsAssignableFrom<Brush>(fixture.BeginButton.Resources["ButtonBackgroundDisabled"]),
            ThemeService.For(fixture.BeginButton).Snapshot[ThemeToken.ControlDisabled], "hidden pending begin resource");
        fixture.BeginWait!.SetResult();
        await DispatcherTurnAsync();
        Assert.True(fixture.BeginButton.IsEnabled);
        await fixture.ShowAsync();
        await ButtonStatesAsync(fixture, fixture.BeginButton);
    }

    private static async Task SelectedVariantSurvivesAccentAndHighContrast(PanelFixture fixture)
    {
        await fixture.ShowAsync(fixture.Started);
        var button = fixture.Field<StackPanel>("_variantButtons").Children.OfType<Button>().First();
        await SelectedVariantAsync(fixture, button);
        ThemeService.Apply(new() { Mode = AppearanceMode.Light, AccentSource = AccentSource.Custom, CustomAccent = "#FFFF00" });
        await SelectedVariantAsync(fixture, button);
        var resources = ThemeService.For(button);
        var transitionField = typeof(ThemeResources).GetField("_transition", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(transitionField);
        var transition = Assert.IsAssignableFrom<System.Collections.ICollection>(transitionField.GetValue(resources));
        ThemeService.SetScanModeAccent(Windows.UI.Color.FromArgb(255, 182, 91, 242), animate: true);
        // Fill can round to its final RGB before the protected text finishes.
        // Wait for the real owner transition to drain and all rendered channels
        // to reach the endpoint; every native presenter/label assertion remains.
        var selectedTokens = new[] { ThemeToken.Selection, ThemeToken.SelectionText, ThemeToken.SelectionBorder };
        await WaitForAnimationAsync(fixture.Root, () => transition.Count == 0 && selectedTokens.All(token =>
            resources[token].Color.Equals(ThemeResources.ToColor(resources.Snapshot[token]))));
        await SelectedVariantAsync(fixture, button);
        var colors = new ContrastColors(new(0, 0, 0), new(255, 255, 255), new(255, 255, 0), new(0, 0, 0), new(128, 128, 128), new(0, 255, 255));
        var contrast = ThemeResolver.Resolve(new(), new(false, new(0, 0, 255), HighContrast: colors));
        fixture.Scope.Apply(contrast, fixture.Scope.Resources.Revision + 1);
        ThemeService.RefreshOwners();
        await SelectedVariantAsync(fixture, button);
        Color(fixture.Root.Background, colors.Background, "synthetic high contrast window");
        Color(Assert.IsAssignableFrom<Brush>(fixture.Combo.Resources["ComboBoxForegroundDisabled"]), colors.Disabled, "high contrast disabled combo");
    }

    private static async Task DisposeDetachesActualOwnerSubscription(PanelFixture fixture)
    {
        await fixture.ShowAsync();
        PanelFixture.ApplyMode(AppearanceMode.Dark);
        var brush = Assert.IsType<SolidColorBrush>(fixture.Combo.Resources["ComboBoxBackground"]);
        var frozen = brush.Color;
        fixture.Panel.Dispose();
        PanelFixture.ApplyMode(AppearanceMode.Light);
        await DispatcherTurnAsync();
        Assert.Equal(frozen, brush.Color);
    }

    private static void PanelPalette(PanelFixture fixture)
    {
        var theme = ThemeService.For(fixture.Root).Snapshot;
        Assert.Equal(theme.IsDark ? ElementTheme.Dark : ElementTheme.Light, fixture.Root.RequestedTheme);
        Color(fixture.Root.Background, theme[ThemeToken.Window], "root surface");
        Color(fixture.Field<TextBlock>("_stateText").Foreground, theme[ThemeToken.TextSecondary], "state text");
        Color(fixture.Field<TextBlock>("_messageText").Foreground, theme[ThemeToken.TextSecondary], "message text");
        var title = Assert.Single(Descendants(fixture.Root).OfType<TextBlock>().Where(text => text.Text == "Identity Vision Bridge 对局控件"));
        Color(title.Foreground, theme[ThemeToken.Text], "actual title foreground");
    }

    private static async Task SelectedVariantAsync(PanelFixture fixture, Button button)
    {
        Assert.False(button.IsEnabled);
        button.ApplyTemplate();
        Assert.True(VisualStateManager.GoToState(button, "Disabled", false));
        await RenderAsync(fixture.Root);
        var presenter = Assert.Single(Descendants(button).OfType<ContentPresenter>());
        var theme = ThemeService.For(button).Snapshot;
        Color(presenter.Background, theme[ThemeToken.Selection], "selected disabled variant fill");
        Color(presenter.Foreground, theme[ThemeToken.SelectionText], "selected disabled variant foreground");
        Color(presenter.BorderBrush, theme[ThemeToken.SelectionBorder], "selected disabled variant border");
        var labels = Descendants(button.Content as DependencyObject).OfType<TextBlock>().ToArray();
        Assert.NotEmpty(labels);
        foreach (var label in labels) Color(label.Foreground, theme[ThemeToken.SelectionText], "selected variant actual label");
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
