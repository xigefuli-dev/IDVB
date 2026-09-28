using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Helps;

public sealed partial class EmphasisGuide
{
    private void InitializePreview()
    {
        _previewLayer.Background = new SolidColorBrush(Color.FromArgb(235, 0, 0, 0));
        _previewLayer.Children.Add(_previewImage);
        var closePreviewButton = new Button
        {
            Content = "✕ 退出展示",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16),
            Padding = new Thickness(16, 10, 16, 10),
            MinHeight = 44,
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 43, 43)),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            closePreviewButton.Resources["ButtonBackground" + state] =
                new SolidColorBrush(Color.FromArgb(255, state == "Pressed" ? (byte)150 : (byte)196, 43, 43));
            closePreviewButton.Resources["ButtonForeground" + state] =
                new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            closePreviewButton.Resources["ButtonBorderBrush" + state] =
                new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        }
        closePreviewButton.Click += (_, _) => HidePreview();
        _previewLayer.Children.Add(closePreviewButton);
        _previewLayer.Tapped += (_, e) =>
        {
            e.Handled = true;
            HidePreview();
        };
    }
}
