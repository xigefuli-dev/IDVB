using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;
internal sealed partial class SurveyLayerPanel : Grid, IDisposable
{

    private void Select(Guid layerId)
    {
        var isControl = IsKeyDown(Windows.System.VirtualKey.Control);
        var isShift = IsKeyDown(Windows.System.VirtualKey.Shift);
        var ordered = CurrentFloorLayers();
        if (isShift && _rangeAnchorLayerId is { } anchor)
        {
            var anchorIndex = Array.FindIndex(ordered, item => item.LayerId == anchor);
            var clickedIndex = Array.FindIndex(ordered, item => item.LayerId == layerId);
            if (anchorIndex >= 0 && clickedIndex >= 0)
            {
                if (!isControl)
                    _selectedLayerIds.Clear();
                for (var index = Math.Min(anchorIndex, clickedIndex);
                     index <= Math.Max(anchorIndex, clickedIndex);
                     index++)
                    _selectedLayerIds.Add(ordered[index].LayerId);
            }
        }
        else if (isControl)
        {
            if (!_selectedLayerIds.Add(layerId))
                _selectedLayerIds.Remove(layerId);
            _rangeAnchorLayerId = layerId;
        }
        else
        {
            _selectedLayerIds.Clear();
            _selectedLayerIds.Add(layerId);
            _rangeAnchorLayerId = layerId;
        }
        _primaryLayerId = _selectedLayerIds.Contains(layerId)
            ? layerId
            : _selectedLayerIds.Count == 0 ? null : _selectedLayerIds.First();
        RaiseSelectionChanged();
        Rebuild();
    }

    private SurveyMapLayer[] CurrentFloorLayers()
    {
        if (_session.Snapshot is not { } snapshot)
            return [];
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase));
        return floor is null
            ? []
            : snapshot.Layers
                .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted)
                .OrderByDescending(item => item.ZOrder)
                .ToArray();
    }

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(
        this,
        new SurveyLayerSelectionEventArgs
        {
            LayerIds = _selectedLayerIds.ToArray(),
            PrimaryLayerId = _primaryLayerId
        });

    private static bool IsKeyDown(Windows.System.VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static NumberBox CreateNumberField(string label, double value, double min, double max) => new()
    {
        Header = label,
        Value = value,
        Minimum = min,
        Maximum = max,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        SmallChange = label.Contains("Scale", StringComparison.Ordinal) ? 0.01d : 1d
    };

    private FrameworkElement CreateLayerSlider(
        string label,
        double value,
        double minimum,
        double maximum,
        Func<double, Task> update)
    {
        var valueText = new TextBlock
        {
            Text = $"{value:F0}%",
            Foreground = new SolidColorBrush(Muted),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var slider = new Slider
        {
            Header = label,
            Value = value,
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = 1d
        };
        slider.ValueChanged += (_, args) =>
        {
            valueText.Text = $"{args.NewValue:F0}%";
        };
        var lastSubmitted = value;
        async Task SubmitAsync()
        {
            var current = slider.Value;
            if (_updating || !double.IsFinite(current) || Math.Abs(current - lastSubmitted) < 0.000001d)
                return;
            lastSubmitted = current;
            await update(current);
        }
        slider.PointerCaptureLost += async (_, _) => await SubmitAsync();
        slider.KeyUp += async (_, _) => await SubmitAsync();
        slider.LostFocus += async (_, _) => await SubmitAsync();
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(slider);
        panel.Children.Add(valueText);
        return panel;
    }

    private static Button SmallButton(string text, string tooltip)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(5),
            Foreground = new SolidColorBrush(Text)
        };
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    private static string LayerStatusText(
        SurveyMapLayer layer,
        SurveyObservation? observation,
        bool dimmedByIsolation)
    {
        var alignment = observation?.State == SurveyObservationState.Registered ? "已对齐" : "未对齐";
        var transform = layer.ManualTransformOverride is null ? "自动" : "手动固定";
        var display = layer.UsesCleanedDisplay ? "已去污" : "原图";
        var masked = layer.HiddenMaskAsset is null ? string.Empty : " · 已遮罩";
        var isolation = dimmedByIsolation ? " · 临时独显压暗" : string.Empty;
        return $"{alignment} · {transform} · {display}{masked} · {layer.Opacity:P0}{isolation}";
    }

    private static Color StatusColor(SurveyObservation? observation) =>
        observation?.State == SurveyObservationState.Registered
            ? Color.FromArgb(255, 38, 145, 85)
            : Color.FromArgb(255, 205, 116, 25);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ++_rebuildGeneration;
        _thumbnailCancellation?.Cancel();
        _thumbnailCancellation = null;
        ClearImageSources(_layerItems);
        ClearImageSources(_properties);
        _thumbnailCache.Clear();
        _layerItems.Children.Clear();
        _properties.Children.Clear();
        Children.Clear();
        _selectedLayerIds.Clear();
        _primaryLayerId = null;
        _rangeAnchorLayerId = null;
        SelectionChanged = null;
        IsolationChanged = null;
    }

    private static void ClearImageSources(DependencyObject root)
    {
        if (root is Image image)
            image.Source = null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            ClearImageSources(VisualTreeHelper.GetChild(root, index));
    }
}
