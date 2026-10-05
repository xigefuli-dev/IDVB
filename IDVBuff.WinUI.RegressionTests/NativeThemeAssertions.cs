using IDVBuff.Appearance;
using IDVBuff.Presentation.Theming;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace IDVBuff.WinUI.RegressionTests;

internal static class NativeThemeAssertions
{
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    public static async Task DispatcherTurnAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () => completion.TrySetResult()));
        await completion.Task.WaitAsync(WaitTimeout);
    }

    public static async Task RenderAsync(FrameworkElement owner)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        void Rendered(object? sender, object args)
        {
            if (++frames == 2) completion.TrySetResult();
        }
        CompositionTarget.Rendering += Rendered;
        try
        {
            owner.InvalidateMeasure();
            owner.UpdateLayout();
            await completion.Task.WaitAsync(WaitTimeout);
        }
        finally { CompositionTarget.Rendering -= Rendered; }
    }

    public static async Task WaitForAnimationAsync(FrameworkElement owner, Func<bool> finished)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Rendered(object? sender, object args)
        {
            if (finished()) completion.TrySetResult();
        }
        CompositionTarget.Rendering += Rendered;
        try
        {
            owner.UpdateLayout();
            if (finished()) completion.TrySetResult();
            await completion.Task.WaitAsync(WaitTimeout);
        }
        finally { CompositionTarget.Rendering -= Rendered; }
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject? parent)
    {
        if (parent is null) yield break;
        yield return parent;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    public static void Color(Brush? brush, RgbColor expected, string context)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        var color = ThemeResources.ToColor(expected);
        Assert.True(solid.Color.Equals(color), $"{context}: actual={solid.Color}; expected={color}");
    }

    public static Dictionary<string, object> NativeBrushes(Control control, string prefix) =>
        control.Resources.Keys.OfType<string>().Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(key => key, key => control.Resources[key]);

    public static void SameBrushes(Control control, IReadOnlyDictionary<string, object> original)
    {
        Assert.NotEmpty(original);
        foreach (var (key, brush) in original) Assert.Same(brush, control.Resources[key]);
    }

    public static async Task ComboStatesAsync(PanelFixture fixture, bool warning)
    {
        var combo = fixture.Combo;
        Assert.True(combo.ApplyTemplate() || combo.IsLoaded);
        foreach (var state in new[] { "Normal", "PointerOver", "Pressed", "Disabled", "Focused", "FocusedPressed" })
        {
            combo.IsEnabled = state != "Disabled";
            Assert.True(VisualStateManager.GoToState(combo, "Unfocused", false));
            Assert.True(VisualStateManager.GoToState(combo, "Normal", false));
            Assert.True(VisualStateManager.GoToState(combo, state, false), "Missing native ComboBox state: " + state);
            await RenderAsync(fixture.Root);
            var nodes = Descendants(combo).ToArray();
            var presenter = Assert.Single(nodes.OfType<ContentPresenter>().Where(node => node.Name == "ContentPresenter"));
            var background = Assert.Single(nodes.OfType<Border>().Where(node => node.Name == "Background"));
            var theme = ThemeService.For(combo).Snapshot;
            var fill = state == "Disabled" ? ThemeToken.ControlDisabled : warning ? ThemeToken.WarningFill :
                state == "PointerOver" ? ThemeToken.ControlHover : state == "Pressed" ? ThemeToken.ControlPressed : ThemeToken.ControlFill;
            var text = state == "Disabled" ? ThemeToken.TextDisabled : ThemeToken.Text;
            Color(background.Background, theme[fill], $"ComboBox/{state}/{warning}/fill");
            Color(presenter.Foreground, theme[text], $"ComboBox/{state}/foreground");
            var selectedLabels = Descendants(presenter).OfType<TextBlock>().Where(node => node.Text is "Healthy" or "Warning").ToArray();
            Assert.NotEmpty(selectedLabels);
            foreach (var label in selectedLabels) Color(label.Foreground, theme[text], $"ComboBox/{state}/actual label");
            if (state != "Disabled") Assert.True(RgbColor.Contrast(theme[text], theme[fill]) >= 4.5);
        }
        combo.IsEnabled = true;
        Assert.True(VisualStateManager.GoToState(combo, "Unfocused", false));
        Assert.True(VisualStateManager.GoToState(combo, "Normal", false));
    }

    public static async Task ButtonStatesAsync(PanelFixture fixture, Button button, bool onlyDisabled = false)
    {
        button.ApplyTemplate();
        foreach (var state in onlyDisabled ? new[] { "Disabled" } : new[] { "Normal", "PointerOver", "Pressed", "Disabled" })
        {
            if (onlyDisabled) Assert.False(button.IsEnabled);
            else button.IsEnabled = state != "Disabled";
            Assert.True(VisualStateManager.GoToState(button, state, false), "Missing native Button state: " + state);
            await RenderAsync(fixture.Root);
            var presenter = Assert.Single(Descendants(button).OfType<ContentPresenter>());
            var theme = ThemeService.For(button).Snapshot;
            var fill = state == "Disabled" ? ThemeToken.ControlDisabled : state == "PointerOver" ? ThemeToken.ControlHover :
                state == "Pressed" ? ThemeToken.ControlPressed : ThemeToken.ControlFill;
            Color(presenter.Background, theme[fill], $"Button/{state}/fill; " + Describe(button));
            Color(presenter.Foreground, theme[state == "Disabled" ? ThemeToken.TextDisabled : ThemeToken.Text], $"Button/{state}/foreground");
        }
        if (!onlyDisabled)
        {
            button.IsEnabled = true;
            Assert.True(VisualStateManager.GoToState(button, "Normal", false));
        }
    }

    public static string Describe(Control control)
    {
        var template = VisualTreeHelper.GetChild(control, 0) as FrameworkElement;
        return $"enabled={control.IsEnabled}; loaded={control.IsLoaded}; theme={control.ActualTheme}; states="
            + string.Join(",", VisualStateManager.GetVisualStateGroups(template).Select(group => group.Name + ":" + group.CurrentState?.Name));
    }
}
