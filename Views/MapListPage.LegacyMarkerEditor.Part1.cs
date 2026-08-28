using IDVBuff.Features.Maps;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Windows.Storage.Pickers;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace IDVBuff.Views;
public sealed partial class MapListPage : UserControl
{

    private Border CreateAnnotationSubPanel()
    {
        var panelLayout = new StackPanel { Spacing = 7 };
        var colorLabel = new TextBlock
        {
            Text = "标记颜色",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 184, 190))
        };
        panelLayout.Children.Add(colorLabel);

        var colorGrid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var i = 0; i < 3; i++)
            colorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            colorGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

        for (var i = 0; i < 9; i++)
        {
            var colorIndex = i;
            var swatch = new Button
            {
                Background = new SolidColorBrush(AnnotationColors[i]),
                BorderBrush = new SolidColorBrush(_selectedAnnotationColor == i
                    ? Color.FromArgb(255, 255, 255, 255)
                    : AnnotationColors[i]),
                BorderThickness = new Thickness(_selectedAnnotationColor == i ? 2 : 1),
                Width = 28,
                Height = 22,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            swatch.Click += (_, _) =>
            {
                _selectedAnnotationColor = colorIndex;
                RefreshMarkerControlPanel();
            };
            Grid.SetRow(swatch, i / 3);
            Grid.SetColumn(swatch, i % 3);
            colorGrid.Children.Add(swatch);
        }
        panelLayout.Children.Add(colorGrid);

        var textButtonColor = AnnotationColors[_selectedAnnotationColor];
        var textButton = CreateMarkerPanelButton("注释文字", textButtonColor);
        if (_selectedAnnotationColor == 8)
            textButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        if (_activeAnnotationType == MapAnnotationType.Text)
        {
            textButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            textButton.BorderThickness = new Thickness(2);
        }
        textButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(textButton);
            _activeAnnotationType = MapAnnotationType.Text;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        panelLayout.Children.Add(textButton);

        var boxButton = CreateMarkerPanelButton("标注框线", AnnotationColors[_selectedAnnotationColor]);
        if (_selectedAnnotationColor == 8)
            boxButton.Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
        if (_activeAnnotationType == MapAnnotationType.Outline)
        {
            boxButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            boxButton.BorderThickness = new Thickness(2);
        }
        boxButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(boxButton);
            _activeAnnotationType = MapAnnotationType.Outline;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        panelLayout.Children.Add(boxButton);

        var activeAnnotations = GetActiveFloorProfile().Annotations.ToList();
        for (var i = 0; i < activeAnnotations.Count; i++)
        {
            var annotation = activeAnnotations[i];
            var number = i + 1;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

            var label = annotation.Type == MapAnnotationType.Text
                ? (string.IsNullOrWhiteSpace(annotation.Text) ? $"文字 {number}" : annotation.Text)
                : $"框线 {number}";
            var labelText = new TextBlock
            {
                Text = label.Length > 8 ? label[..8] : label,
                FontSize = 11,
                Foreground = new SolidColorBrush(AnnotationColors[annotation.ColorIndex]),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(labelText);

            var capturedId = annotation.Id;
            var deleteButton = CreateMarkerPanelButton("X", Color.FromArgb(255, 255, 90, 66));
            deleteButton.Padding = new Thickness(0);
            deleteButton.Click += (_, _) => DeleteAnnotation(capturedId);
            Grid.SetColumn(deleteButton, 1);
            row.Children.Add(deleteButton);

            panelLayout.Children.Add(row);
        }

        return new Border
        {
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(140, 16, 24, 34)),
            CornerRadius = new CornerRadius(8),
            Child = panelLayout
        };
    }

    private void DeleteAnnotation(Guid id)
    {
        var profile = GetActiveFloorProfile();
        profile.Annotations.RemoveAll(a => a.Id == id);
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

    private async Task CommitTextAnnotationAsync(NormalizedRectangle bounds)
    {
        var textBox = new TextBox
        {
            PlaceholderText = "输入注释文字…",
            AcceptsReturn = false,
            Height = 36
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "输入注释文字",
            Content = textBox,
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || string.IsNullOrWhiteSpace(textBox.Text))
        {
            _activeAnnotationType = default;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
            return;
        }

        GetActiveFloorProfile().Annotations.Add(new MapAnnotation
        {
            Type = MapAnnotationType.Text,
            ColorIndex = _selectedAnnotationColor,
            Bounds = bounds.Clone(),
            Text = textBox.Text.Trim()
        });
        _activeAnnotationType = default;
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }
}
