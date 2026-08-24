using IDVBuff.PluginContracts;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;

namespace IDVBuff.Features.Plugins;

public sealed partial class TeachingTipManager
{
    private static bool TryGetPluginModifier(
        Windows.System.VirtualKey key,
        out PluginInputModifiers modifier)
    {
        modifier = (uint)key switch
        {
            0x10 or 0xA0 or 0xA1 => PluginInputModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => PluginInputModifiers.Control,
            0x12 or 0xA4 or 0xA5 => PluginInputModifiers.Alt,
            0x5B or 0x5C => PluginInputModifiers.Windows,
            _ => PluginInputModifiers.None
        };
        return modifier != PluginInputModifiers.None;
    }

    private static PluginInputModifiers ReadCurrentPluginModifiers(
        PluginInputModifiers observed)
    {
        var modifiers = observed;
        if (IsCurrentKeyDown(Windows.System.VirtualKey.Control))
            modifiers |= PluginInputModifiers.Control;
        if (IsCurrentKeyDown(Windows.System.VirtualKey.Menu))
            modifiers |= PluginInputModifiers.Alt;
        if (IsCurrentKeyDown(Windows.System.VirtualKey.Shift))
            modifiers |= PluginInputModifiers.Shift;
        if (IsCurrentKeyDown(Windows.System.VirtualKey.LeftWindows)
            || IsCurrentKeyDown(Windows.System.VirtualKey.RightWindows))
        {
            modifiers |= PluginInputModifiers.Windows;
        }
        return modifiers;
    }

    private static bool IsCurrentKeyDown(Windows.System.VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & CoreVirtualKeyStates.Down) != 0;

    private static bool TryGetPluginMouseButton(
        Microsoft.UI.Input.PointerPointProperties properties,
        out PluginMouseButton button)
    {
        if (properties.IsLeftButtonPressed)
            button = PluginMouseButton.Left;
        else if (properties.IsRightButtonPressed)
            button = PluginMouseButton.Right;
        else if (properties.IsMiddleButtonPressed)
            button = PluginMouseButton.Middle;
        else if (properties.IsXButton1Pressed)
            button = PluginMouseButton.XButton1;
        else if (properties.IsXButton2Pressed)
            button = PluginMouseButton.XButton2;
        else
        {
            button = default;
            return false;
        }
        return true;
    }

    private static object? ReadProviderValue(
        IPluginSettingsProvider provider, IPluginSetting setting)
    {
        try
        {
            return provider.GetSettingValue(setting.Key);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"TTM 读取设置值失败 {setting.Key}: {exception}");
            return null;
        }
    }

    private static double CoerceSlider(double value, PluginSliderSetting slider)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = slider.DefaultValue;
        return Math.Clamp(value, slider.Minimum, slider.Maximum);
    }

    private static double SnapSliderValue(double value, PluginSliderSetting slider)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = slider.Minimum;
        if (slider.StepFrequency > 0)
            value = Math.Round(value / slider.StepFrequency) * slider.StepFrequency;
        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        return Math.Round(value, 3);
    }

    private static bool IsWithinInteractiveControl(
        DependencyObject source,
        DependencyObject focusTarget,
        DependencyObject boundary)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, boundary))
                return false;
            if (!ReferenceEquals(current, focusTarget)
                && current is Microsoft.UI.Xaml.Controls.Control)
            {
                return true;
            }
        }
        return false;
    }

    private static void AttachBlankAreaEditingHandler(
        Microsoft.UI.Xaml.Controls.TeachingTip tip,
        FrameworkElement content)
    {
        tip.AddHandler(UIElement.PointerPressedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, args) =>
            {
                if (args.OriginalSource is DependencyObject source
                    && !IsWithinInteractiveControl(source, content, tip)
                    && content.Tag is Action commitAndEndEditing)
                    commitAndEndEditing();
            }), handledEventsToo: true);
    }

    private static void AttachNumericCommit(
        FrameworkElement content,
        IEnumerable<(Microsoft.UI.Xaml.Controls.NumberBox Input, Action Commit)> editors,
        Action endEditing)
    {
        content.Tag = new Action(() =>
        {
            foreach (var editor in editors)
                editor.Commit();
            endEditing();
        });
    }

}
