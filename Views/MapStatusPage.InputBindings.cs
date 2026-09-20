using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace IDVBuff.Views;

public sealed partial class MapStatusPage
{
    /// <summary>Returns the authored button for an onboarding step to highlight.</summary>
    public FrameworkElement? GetBindingControl(MapRuntimeBindingTarget target) =>
        _bindingRows.TryGetValue(target, out var row) ? row as FrameworkElement : null;

    /// <summary>Returns the runtime master switch for onboarding emphasis.</summary>
    public FrameworkElement GetRuntimeEnableControl() => _enabledToggle;

    /// <summary>Returns the map-display calibration action for onboarding emphasis.</summary>
    public FrameworkElement? GetMapViewportCalibrationControl() =>
        FindDescendantButton(this, "校准地图区域");

    private static Button? FindDescendantButton(DependencyObject root, string content)
    {
        if (root is Button { Content: string text } button && text == content)
            return button;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindDescendantButton(VisualTreeHelper.GetChild(root, index), content) is { } child)
                return child;
        }

        return null;
    }

    private async void BindingButton_Click(MapRuntimeBindingTarget target)
    {
        var binding = GetBinding(target);
        if (!binding.IsConfigured)
        {
            BeginRecording(target);
            return;
        }

        _recording = null;
        _recordingHeldKeys.Clear();
        _recordingTriggerKey = 0;
        try
        {
            var resetTask = _runtime.SetBindingAsync(target, new MapInputBinding());
            RefreshBindingButtonAppearance(target);
            Refresh();
            await resetTask;
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        RefreshBindingButtonAppearance(target);
        Refresh();
    }

    private MapInputBinding GetBinding(MapRuntimeBindingTarget target) => target switch
    {
        MapRuntimeBindingTarget.QuickScan => _runtime.Settings.QuickScanBinding,
        MapRuntimeBindingTarget.OverlayToggle => _runtime.Settings.OverlayToggleBinding,
        MapRuntimeBindingTarget.ManualRecognition => _runtime.Settings.ManualRecognitionBinding,
        MapRuntimeBindingTarget.GameMapToggle => _runtime.Settings.GameMapToggleBinding,
        MapRuntimeBindingTarget.ControlPanelToggle => _runtime.Settings.ControlPanelToggleBinding,
        MapRuntimeBindingTarget.SwitchFloor => _runtime.Settings.SwitchFloorBinding,
        MapRuntimeBindingTarget.SaveMapCache => _runtime.Settings.SaveMapCacheBinding,
        MapRuntimeBindingTarget.RestMapDisplay => _runtime.Settings.RestMapDisplayBinding,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
    };

    private void RefreshBindingButtonAppearance(MapRuntimeBindingTarget target)
    {
        if (!_bindingButtons.TryGetValue(target, out var button))
            return;

        var isRecording = _recording == target;
        var isConfigured = GetBinding(target).IsConfigured;
        var showReset = !isRecording && isConfigured
            && _bindingButtonHovered.GetValueOrDefault(target);
        var background = isRecording
            ? new SolidColorBrush(Color.FromArgb(255, 22, 62, 115))
            : !isConfigured
                ? new SolidColorBrush(Color.FromArgb(255, 46, 132, 225))
            : showReset
                ? new SolidColorBrush(Color.FromArgb(255, 196, 55, 55))
                : new SolidColorBrush(Color.FromArgb(255, 242, 242, 242));
        var foreground = isRecording || !isConfigured || showReset
            ? new SolidColorBrush(Color.FromArgb(255, 255, 255, 255))
            : new SolidColorBrush(Color.FromArgb(255, 32, 32, 32));
        var border = isRecording
            ? new SolidColorBrush(Color.FromArgb(255, 14, 43, 82))
            : !isConfigured
                ? new SolidColorBrush(Color.FromArgb(255, 30, 105, 180))
            : showReset
                ? new SolidColorBrush(Color.FromArgb(255, 160, 35, 35))
                : new SolidColorBrush(Color.FromArgb(255, 218, 218, 218));

        button.Content = isRecording
            ? "请按按键…"
            : showReset ? "重置按键" : "设置按键";
        button.Background = background;
        button.Foreground = foreground;
        button.BorderBrush = border;

        // WinUI's default Button template replaces these properties in its
        // PointerOver/Pressed visual states. Override the local resources as
        // well so the reset state remains visibly red.
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonBackgroundPointerOver"] = background;
        button.Resources["ButtonBackgroundPressed"] = background;
        button.Resources["ButtonBorderBrush"] = border;
        button.Resources["ButtonBorderBrushPointerOver"] = border;
        button.Resources["ButtonBorderBrushPressed"] = border;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;

        // The default WinUI template animates the inner ContentPresenter,
        // which can run after PointerEntered and overwrite Button.Background.
        // Apply the same colors after that visual-state transition as well.
        button.ApplyTemplate();
        button.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ApplyButtonContentPresenterColors(button, background, border, foreground));
    }

    private static void ApplyButtonContentPresenterColors(
        Button button,
        SolidColorBrush background,
        SolidColorBrush border,
        SolidColorBrush foreground)
    {
        if (FindContentPresenter(button) is not { } presenter)
            return;

        presenter.Background = background;
        presenter.BorderBrush = border;
        presenter.Foreground = foreground;
    }

    private static ContentPresenter? FindContentPresenter(DependencyObject root)
    {
        if (root is ContentPresenter presenter)
            return presenter;

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindContentPresenter(VisualTreeHelper.GetChild(root, index)) is { } child)
                return child;
        }

        return null;
    }

    private static bool TryGetModifier(uint key, out MapInputModifiers modifier)
    {
        modifier = key switch
        {
            0x10 or 0xA0 or 0xA1 => MapInputModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => MapInputModifiers.Control,
            0x12 or 0xA4 or 0xA5 => MapInputModifiers.Alt,
            0x5B or 0x5C => MapInputModifiers.Windows,
            _ => MapInputModifiers.None
        };
        return modifier != MapInputModifiers.None;
    }

    private void BindingScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_root is null)
            return;

        var pointerPosition = e.GetCurrentPoint(_root).Position;
        // Scrolling moves the controls below a stationary pointer without
        // raising PointerEntered/PointerExited. Run after the ScrollViewer has
        // applied its offset, then re-evaluate which binding button is below it.
        _root.DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => RefreshBindingButtonHoverStates(pointerPosition));
    }

    private void RefreshBindingButtonHoverStates(Windows.Foundation.Point pointerPosition)
    {
        if (_root is null)
            return;

        foreach (var (target, button) in _bindingButtons)
        {
            var topLeft = button.TransformToVisual(_root)
                .TransformPoint(new Windows.Foundation.Point());
            var isHovered = button.Visibility == Visibility.Visible
                && pointerPosition.X >= topLeft.X
                && pointerPosition.X <= topLeft.X + button.ActualWidth
                && pointerPosition.Y >= topLeft.Y
                && pointerPosition.Y <= topLeft.Y + button.ActualHeight;
            if (_bindingButtonHovered.GetValueOrDefault(target) == isHovered)
                continue;

            _bindingButtonHovered[target] = isHovered;
            RefreshBindingButtonAppearance(target);
        }
    }

    private async void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_recording is null)
            return;
        e.Handled = true;
        var key = (uint)e.Key;
        if (!_recordingHeldKeys.Add(key))
            return;
        _recordingTriggerKey = key;
    }

    private async void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_recording is null)
            return;

        e.Handled = true;
        var key = (uint)e.Key;
        if (!_recordingHeldKeys.Contains(key))
            return;
        await SaveRecordedBindingAsync(CreateKeyboardBinding(
            _recordingTriggerKey,
            _recordingHeldKeys.Where(held => held != _recordingTriggerKey)));
    }

    private async void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_recording is null)
            return;
        var properties = e.GetCurrentPoint(_root).Properties;
        var button = properties.IsLeftButtonPressed
            ? MapMouseButton.Left
            : properties.IsRightButtonPressed
                ? MapMouseButton.Right
                : properties.IsMiddleButtonPressed
                    ? MapMouseButton.Middle
                    : properties.IsXButton1Pressed
                        ? MapMouseButton.XButton1
                        : MapMouseButton.XButton2;
        e.Handled = true;
        await SaveRecordedBindingAsync(new MapInputBinding
        {
            Kind = MapInputBindingKind.Mouse,
            MouseButton = button
        });
    }

    private async Task SaveRecordedBindingAsync(MapInputBinding binding)
    {
        if (_recording is not { } target)
            return;
        _recording = null;
        _recordingHeldKeys.Clear();
        _recordingTriggerKey = 0;
        try
        {
            await _runtime.SetBindingAsync(target, binding);
        }
        catch (Exception exception)
        {
            _status.Text = exception.Message;
        }
        Refresh();
    }

    private static MapInputBinding CreateKeyboardBinding(
        uint virtualKey,
        IEnumerable<uint> heldKeys)
    {
        var modifiers = MapInputModifiers.None;
        var companions = new List<uint>();
        foreach (var heldKey in heldKeys)
        {
            if (TryGetModifier(heldKey, out var modifier))
                modifiers |= modifier;
            else
                companions.Add(heldKey);
        }
        return new MapInputBinding
        {
            Kind = MapInputBindingKind.Keyboard,
            VirtualKey = virtualKey,
            Modifiers = modifiers,
            CompanionVirtualKeys = companions
        };
    }
}
