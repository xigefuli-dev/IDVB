using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace IDVBuff.Features.Maps;

public sealed partial class MapControlPanelWindow
{
    private void RebuildClassItems()
    {
        _classComboBox.Items.Clear();
        foreach (var diagnostic in _mapClassDiagnostics)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = diagnostic.MapClass,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!diagnostic.IsHealthy)
            {
                var warning = new FontIcon
                {
                    Glyph = "\uE7BA",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 185, 0)),
                    FontSize = 15,
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(warning, diagnostic.Summary);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(
                    warning, diagnostic.Summary);
                Grid.SetColumn(warning, 1);
                row.Children.Add(warning);
            }
            _classComboBox.Items.Add(new ComboBoxItem
            {
                Tag = diagnostic,
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            });
        }
        _renderedMapClassDiagnostics = _mapClassDiagnostics;
    }

    private void OnDiagnosticSnapshotChanged()
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
            return;
        dispatcher.TryEnqueue(() =>
        {
            var snapshot = MapClassDiagnosticCoordinator.Instance.Snapshot;
            _mapClassDiagnostics = _mapClasses.Select(mapClass =>
                snapshot.TryGetValue(mapClass, out var diagnostic)
                    ? diagnostic
                    : new MapClassDiagnostic(mapClass, true, [])).ToArray();
            Refresh(_snapshot);
        });
    }
}
