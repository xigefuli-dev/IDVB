using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Features.Plugins;

internal static class PluginNumericInput
{
    public static bool TryGetValue(NumberBox numberBox, out double value)
    {
        value = default;
        numberBox.ApplyTemplate();
        return FindTextBox(numberBox) is { } textBox
            && double.TryParse(textBox.Text, NumberStyles.Float,
                CultureInfo.CurrentCulture, out value)
            && double.IsFinite(value);
    }

    public static void Attach(
        NumberBox numberBox,
        Action<double> valueChanged,
        Action? enterPressed = null,
        Action? cancelPressed = null)
    {
        RoutedEventHandler? loaded = null;
        loaded = (_, _) =>
        {
            numberBox.ApplyTemplate();
            if (FindTextBox(numberBox) is not { } textBox) return;
            numberBox.Loaded -= loaded;
            var valueBeforeEditing = numberBox.Value;
            textBox.GotFocus += (_, _) =>
            {
                if (double.IsFinite(numberBox.Value))
                    valueBeforeEditing = numberBox.Value;
            };
            textBox.TextChanged += (_, _) =>
            {
                if (double.TryParse(textBox.Text, NumberStyles.Float,
                        CultureInfo.CurrentCulture, out var value)
                    && double.IsFinite(value))
                {
                    valueChanged(value);
                }
            };
            textBox.KeyDown += (_, args) =>
            {
                if (args.Key == Windows.System.VirtualKey.Escape)
                {
                    numberBox.Value = valueBeforeEditing;
                    valueChanged(valueBeforeEditing);
                    cancelPressed?.Invoke();
                    args.Handled = true;
                    return;
                }
                if (args.Key != Windows.System.VirtualKey.Enter) return;
                if (enterPressed is not null)
                    enterPressed();
                else if (double.TryParse(textBox.Text, NumberStyles.Float,
                             CultureInfo.CurrentCulture, out var value)
                         && double.IsFinite(value))
                {
                    valueChanged(value);
                }
                args.Handled = true;
            };
        };
        numberBox.Loaded += loaded;
    }

    private static TextBox? FindTextBox(DependencyObject root)
    {
        if (root is TextBox textBox) return textBox;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            if (FindTextBox(VisualTreeHelper.GetChild(root, index)) is { } child)
                return child;
        }
        return null;
    }
}
