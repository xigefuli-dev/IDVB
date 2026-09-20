using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace IDVBuff.Survey.Editor.WinUI;

public sealed partial class SurveyProjectEditor
{
    private UIElement? _keyboardContentRoot;

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void InitializeKeyboardNavigation()
    {
        _keyboardContentRoot = Content as UIElement;
        AddKeyboardHandlers(this);
        if (_keyboardContentRoot is not null)
            AddKeyboardHandlers(_keyboardContentRoot);
        LostFocus += Editor_LostFocus;
    }

    private void DisposeKeyboardNavigation()
    {
        RemoveKeyboardHandlers(this);
        if (_keyboardContentRoot is not null)
            RemoveKeyboardHandlers(_keyboardContentRoot);
        _keyboardContentRoot = null;
        LostFocus -= Editor_LostFocus;
    }

    private void AddKeyboardHandlers(UIElement element)
    {
        element.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Editor_KeyDown), true);
        element.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(Editor_KeyUp), true);
        element.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(Editor_KeyDown), true);
        element.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler(Editor_KeyUp), true);
    }

    private void RemoveKeyboardHandlers(UIElement element)
    {
        element.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Editor_KeyDown));
        element.RemoveHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(Editor_KeyUp));
        element.RemoveHandler(UIElement.KeyDownEvent, new KeyEventHandler(Editor_KeyDown));
        element.RemoveHandler(UIElement.KeyUpEvent, new KeyEventHandler(Editor_KeyUp));
    }

    private void Layers_SelectionChanged(
        object? sender,
        SurveyLayerSelectionEventArgs args) =>
        _canvas.SelectLayers(args.LayerIds, args.PrimaryLayerId);

    private void Canvas_LayerSelected(object? sender, Guid layerId) =>
        _layers.SelectLayer(layerId);

    private void Layers_IsolationChanged(
        object? sender,
        SurveyLayerIsolationChangedEventArgs args) =>
        _canvas.SetIsolatedLayer(args.LayerId);

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space)
            return;
        e.Handled = true;
        _canvas.BeginTemporaryNavigation();
    }

    private void Editor_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space)
            return;
        e.Handled = true;
        _canvas.EndTemporaryNavigation();
    }

    private void Editor_LostFocus(object sender, RoutedEventArgs e) =>
        _canvas.EndTemporaryNavigation();
}
