using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using IDVBuff.UpdateCore;
using Microsoft.UI;

namespace IDVBuff.Views;
/// <summary>Product, licensing, privacy, and attribution information.</summary>
public sealed partial class SettingsPage : Page
{

    private async void SpecificationCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ValueTuple<string, string> details })
            return;

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = details.Item1,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text = details.Item2,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 14
                }
            },
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }
}
