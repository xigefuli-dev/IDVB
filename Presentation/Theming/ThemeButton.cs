using System.Runtime.CompilerServices;
using IDVBuff.Appearance;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Presentation.Theming;

internal static class ThemeButton
{
    private static readonly ConditionalWeakTable<Button, Binding> Bindings = new();

    public static Button Apply(Button button, ThemeButtonRole role)
    {
        var binding = Bindings.GetValue(button, target => new Binding(target));
        binding.Role = role;
        binding.Refresh(ThemeService.For(button).Snapshot);
        return button;
    }

    private sealed class Binding
    {
        private readonly (SolidColorBrush Fill, SolidColorBrush Border, SolidColorBrush Text)[] _states =
            Enumerable.Range(0, 4).Select(_ => (new SolidColorBrush(), new SolidColorBrush(), new SolidColorBrush())).ToArray();
        public ThemeButtonRole Role { get; set; }

        public Binding(Button button)
        {
            var suffixes = new[] { "", "PointerOver", "Pressed", "Disabled" };
            for (var i = 0; i < suffixes.Length; i++)
            foreach (var prefix in new[] { "Button", "AccentButton" })
            {
                button.Resources[prefix + "Background" + suffixes[i]] = _states[i].Fill;
                button.Resources[prefix + "BorderBrush" + suffixes[i]] = _states[i].Border;
                button.Resources[prefix + "Foreground" + suffixes[i]] = _states[i].Text;
            }
            button.Background = _states[0].Fill;
            button.BorderBrush = _states[0].Border;
            button.Foreground = _states[0].Text;
            // Stable brush identities also update an already-hovered template.
            // No delayed presenter writes that can restore an earlier state.
            ThemeService.For(button).Changed += Refresh;
        }

        public void Refresh(ThemeSnapshot theme)
        {
            var palette = ButtonPalette.Resolve(theme, Role);
            var colors = new[] { palette.Normal, palette.Hover, palette.Pressed, palette.Disabled };
            for (var i = 0; i < colors.Length; i++)
            {
                _states[i].Fill.Color = ThemeResources.ToColor(colors[i].Fill);
                _states[i].Border.Color = ThemeResources.ToColor(colors[i].Border);
                _states[i].Text.Color = ThemeResources.ToColor(colors[i].Text);
            }
        }
    }
}
