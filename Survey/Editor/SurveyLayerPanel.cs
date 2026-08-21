using IDVBuff.Survey.Domain;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace IDVBuff.Survey.Editor.WinUI;

internal sealed class SurveyLayerSelectionEventArgs : EventArgs
{
    public required IReadOnlyCollection<Guid> LayerIds { get; init; }
    public Guid? PrimaryLayerId { get; init; }
}

internal sealed class SurveyLayerIsolationChangedEventArgs : EventArgs
{
    public Guid? LayerId { get; init; }
}

internal sealed class SurveyLayerPanel : Grid, IDisposable
{
    private static readonly Color Panel = Color.FromArgb(255, 18, 27, 39);
    private static readonly Color Raised = Color.FromArgb(255, 25, 36, 50);
    private static readonly Color Border = Color.FromArgb(255, 46, 62, 79);
    private static readonly Color Text = Color.FromArgb(255, 226, 234, 245);
    private static readonly Color Muted = Color.FromArgb(255, 151, 166, 187);
    private readonly SurveyEditorSession _session;
    private readonly StackPanel _layerItems = new() { Spacing = 6 };
    private readonly StackPanel _properties = new() { Spacing = 8 };
    private readonly SemaphoreSlim _thumbnailGate = new(4, 4);
    private readonly Dictionary<(Guid LayerId, string ContentKey), Microsoft.UI.Xaml.Media.Imaging.BitmapImage>
        _thumbnailCache = [];
    private CancellationTokenSource? _thumbnailCancellation;
    private string _floorKey = "1f";
    private readonly HashSet<Guid> _selectedLayerIds = [];
    private Guid? _primaryLayerId;
    private Guid? _rangeAnchorLayerId;
    private Guid? _isolatedLayerId;
    private bool _updating;
    private int _rebuildGeneration;
    private bool _keepAspectRatio = true;
    private Guid? _draggedLayerId;
    private bool _disposed;

    public SurveyLayerPanel(SurveyEditorSession session)
    {
        _session = session;
        Width = 286;
        Margin = new Thickness(0, 4, 0, 0);
        Background = new SolidColorBrush(Panel);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var listArea = new Grid { MinHeight = 0 };
        listArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "地图图片 / 测绘图层",
            FontSize = 16,
            Margin = new Thickness(12, 11, 12, 9),
            Foreground = new SolidColorBrush(Text)
        };
        listArea.Children.Add(title);
        var listScroll = new ScrollViewer
        {
            Content = _layerItems,
            Padding = new Thickness(10, 0, 10, 10),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(listScroll, 1);
        listArea.Children.Add(listScroll);
        Children.Add(listArea);
        var propertyScroll = new ScrollViewer
        {
            Content = _properties,
            Padding = new Thickness(10),
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(propertyScroll, 1);
        Children.Add(propertyScroll);
    }

    public event EventHandler<SurveyLayerSelectionEventArgs>? SelectionChanged;
    public event EventHandler<SurveyLayerIsolationChangedEventArgs>? IsolationChanged;
    public IReadOnlyCollection<Guid> SelectedLayerIds => _selectedLayerIds;
    public Guid? PrimaryLayerId => _primaryLayerId;
    public Guid? IsolatedLayerId => _isolatedLayerId;

    public void SetFloor(string floorKey)
    {
        if (!string.Equals(_floorKey, floorKey, StringComparison.OrdinalIgnoreCase))
        {
            SetIsolatedLayer(null);
            _selectedLayerIds.Clear();
            _primaryLayerId = null;
            _rangeAnchorLayerId = null;
        }
        _floorKey = floorKey;
        Rebuild();
    }

    private void SetIsolatedLayer(Guid? layerId)
    {
        if (_isolatedLayerId == layerId)
            return;
        _isolatedLayerId = layerId;
        IsolationChanged?.Invoke(this, new SurveyLayerIsolationChangedEventArgs { LayerId = layerId });
        if (!_disposed)
            Rebuild();
    }

    private async Task CycleVisibilityAsync(SurveyMapLayer layer)
    {
        if (_isolatedLayerId == layer.LayerId)
        {
            SetIsolatedLayer(null);
            return;
        }
        if (!layer.IsVisible)
        {
            await _session.EditAsync(layer.LayerId, state => state with { IsVisible = true });
            if (_session.Snapshot?.Layers.FirstOrDefault(item => item.LayerId == layer.LayerId)?.IsVisible == true)
                SetIsolatedLayer(layer.LayerId);
            return;
        }
        await _session.EditAsync(layer.LayerId, state => state with { IsVisible = false });
    }

    public void SelectLayer(Guid? layerId)
    {
        _selectedLayerIds.Clear();
        if (layerId is { } id)
            _selectedLayerIds.Add(id);
        _primaryLayerId = layerId;
        _rangeAnchorLayerId = layerId;
        RaiseSelectionChanged();
        Rebuild();
    }

    public void Rebuild()
    {
        if (_disposed)
            return;
        var previousCancellation = _thumbnailCancellation;
        var thumbnailCancellation = new CancellationTokenSource();
        _thumbnailCancellation = thumbnailCancellation;
        previousCancellation?.Cancel();
        var generation = ++_rebuildGeneration;
        if (_session.Snapshot is not { } snapshot)
            return;
        var floor = snapshot.Floors.FirstOrDefault(item =>
            string.Equals(item.FloorKey, _floorKey, StringComparison.OrdinalIgnoreCase));
        var layers = floor is null
            ? []
            : snapshot.Layers
                .Where(item => item.FloorId == floor.FloorId && !item.IsDeleted)
                .OrderByDescending(item => item.ZOrder)
                .ToArray();
        if (_isolatedLayerId is { } isolated
            && layers.FirstOrDefault(item => item.LayerId == isolated) is not { IsVisible: true })
            SetIsolatedLayer(null);
        var observations = snapshot.Observations.ToDictionary(item => item.ObservationId);
        var validThumbnailKeys = layers
            .Where(layer => observations.ContainsKey(layer.ObservationId))
            .Select(layer => (
                layer.LayerId,
                ThumbnailContentKey(layer, observations[layer.ObservationId])))
            .ToHashSet();
        foreach (var staleKey in _thumbnailCache.Keys
                     .Where(key => !validThumbnailKeys.Contains(key))
                     .ToArray())
        {
            _thumbnailCache.Remove(staleKey);
        }
        _selectedLayerIds.RemoveWhere(id => layers.All(item => item.LayerId != id));
        if (_selectedLayerIds.Count == 0 && layers.FirstOrDefault() is { } first)
        {
            _selectedLayerIds.Add(first.LayerId);
            _primaryLayerId = first.LayerId;
            _rangeAnchorLayerId = first.LayerId;
        }
        if (_primaryLayerId is null || !_selectedLayerIds.Contains(_primaryLayerId.Value))
            _primaryLayerId = _selectedLayerIds.Count == 0 ? null : _selectedLayerIds.First();
        _layerItems.Children.Clear();
        foreach (var layer in layers)
            _layerItems.Children.Add(CreateLayerRow(
                layer,
                observations.GetValueOrDefault(layer.ObservationId),
                generation,
                thumbnailCancellation.Token));
        if (layers.Length == 0)
        {
            _layerItems.Children.Add(new TextBlock
            {
                Text = "当前楼层没有活动图层。可使用撤销恢复刚删除的图层。",
                Margin = new Thickness(8, 16, 8, 16),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Muted)
            });
        }
        BuildProperties(layers.FirstOrDefault(item => item.LayerId == _primaryLayerId));
        RaiseSelectionChanged();
    }

    private FrameworkElement CreateLayerRow(
        SurveyMapLayer layer,
        SurveyObservation? observation,
        int generation,
        CancellationToken cancellationToken)
    {
        var root = new Grid
        {
            Padding = new Thickness(7),
            ColumnSpacing = 6,
            CanDrag = true,
            AllowDrop = true,
            Background = new SolidColorBrush(_selectedLayerIds.Contains(layer.LayerId)
                ? layer.LayerId == _primaryLayerId
                    ? Color.FromArgb(255, 28, 67, 103)
                    : Color.FromArgb(255, 25, 74, 76)
                : Raised)
        };
        root.DragStarting += (_, _) => _draggedLayerId = layer.LayerId;
        root.Tapped += (_, _) => Select(layer.LayerId);
        root.DragOver += (_, args) => args.AcceptedOperation = DataPackageOperation.Move;
        root.Drop += async (_, _) =>
        {
            if (_draggedLayerId is { } dragged)
                await _session.MoveLayerBeforeAsync(dragged, layer.LayerId);
            _draggedLayerId = null;
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var isIsolated = _isolatedLayerId == layer.LayerId;
        var isDimmedByIsolation = _isolatedLayerId is not null
            && !isIsolated
            && layer.IsVisible;
        var visible = SmallButton(
            isIsolated ? "\uD83D\uDC41" : layer.IsVisible ? "●" : "○",
            isIsolated
                ? "退出独显，恢复其他图层的原有显示状态"
                : layer.IsVisible
                    ? "隐藏图层（持久状态）"
                    : "恢复可见并独显当前图层");
        visible.Click += async (_, _) => await CycleVisibilityAsync(layer);
        root.Children.Add(visible);

        var thumbnail = new Image
        {
            Width = 50,
            Height = 38,
            Stretch = Stretch.UniformToFill,
            Opacity = isDimmedByIsolation ? 0.38d : layer.IsVisible ? 1d : 0.45d
        };
        if (observation is not null)
        {
            var thumbnailKey = (layer.LayerId, ThumbnailContentKey(layer, observation));
            if (_thumbnailCache.TryGetValue(thumbnailKey, out var cached))
                thumbnail.Source = cached;
            else
                _ = LoadThumbnailAsync(
                    thumbnail,
                    layer.LayerId,
                    thumbnailKey,
                    generation,
                    cancellationToken);
        }
        Grid.SetColumn(thumbnail, 1);
        root.Children.Add(thumbnail);

        var info = new StackPanel { Spacing = 2 };
        info.Children.Add(new TextBlock
        {
            Text = layer.Name,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Text)
        });
        info.Children.Add(new TextBlock
        {
            Text = LayerStatusText(layer, observation, isDimmedByIsolation),
            FontSize = 11,
            Foreground = new SolidColorBrush(StatusColor(observation))
        });
        Grid.SetColumn(info, 2);
        root.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var up = SmallButton("↑", "移到上一层");
        up.Click += async (_, _) => await _session.MoveLayerAsync(layer.LayerId, towardTop: true);
        actions.Children.Add(up);
        var down = SmallButton("↓", "移到下一层");
        down.Click += async (_, _) => await _session.MoveLayerAsync(layer.LayerId, towardTop: false);
        actions.Children.Add(down);
        var remove = SmallButton("×", "删除图层（可撤销）");
        remove.Click += async (_, _) =>
        {
            if (_isolatedLayerId == layer.LayerId)
                SetIsolatedLayer(null);
            await _session.EditAsync(layer.LayerId, state => state with { IsDeleted = true });
        };
        actions.Children.Add(remove);
        Grid.SetColumn(actions, 3);
        root.Children.Add(actions);
        return new Border
        {
            BorderBrush = new SolidColorBrush(Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = root
        };
    }

    private async Task LoadThumbnailAsync(
        Image target,
        Guid layerId,
        (Guid LayerId, string ContentKey) thumbnailKey,
        int generation,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await _thumbnailGate.WaitAsync(cancellationToken);
            entered = true;
            var bitmap = await SurveyBitmapLoader.LoadLayerAsync(
                _session,
                layerId,
                decodePixelWidth: 100,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _thumbnailCache[thumbnailKey] = bitmap;
            if (!_disposed && generation == _rebuildGeneration)
                target.Source = bitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A failed thumbnail must not make layer editing unavailable.
        }
        finally
        {
            if (entered)
                _thumbnailGate.Release();
        }
    }

    private static string ThumbnailContentKey(
        SurveyMapLayer layer,
        SurveyObservation observation)
    {
        var displayAsset = layer.ColorFilterAsset
            ?? (layer.UsesCleanedDisplay && observation.DisplayAsset is not null
                ? observation.DisplayAsset
                : observation.SourceAsset);
        return $"{displayAsset.Sha256}:{layer.HiddenMaskAsset?.Sha256}:{layer.Brightness:R}";
    }

    private void BuildProperties(SurveyMapLayer? layer)
    {
        _updating = true;
        _properties.Children.Clear();
        if (layer is null)
        {
            _properties.Children.Add(new TextBlock
            {
                Text = "未选择图层",
                Foreground = new SolidColorBrush(Muted)
            });
            _updating = false;
            return;
        }

        _properties.Children.Add(new TextBlock
        {
            Text = $"图层属性 · 已选择 {_selectedLayerIds.Count} 层",
            FontSize = 15,
            Foreground = new SolidColorBrush(Text)
        });
        var name = new TextBox { Header = "图层名称", Text = layer.Name };
        name.LostFocus += async (_, _) =>
        {
            if (!_updating && !string.IsNullOrWhiteSpace(name.Text) && name.Text != layer.Name)
                await _session.EditAsync(layer.LayerId, state => state with { Name = name.Text.Trim() });
        };
        _properties.Children.Add(name);
        var transform = layer.EffectiveTransform;
        AddTransformField("X", transform.TranslationX, value => transform with { TranslationX = value });
        AddTransformField("Y", transform.TranslationY, value => transform with { TranslationY = value });
        AddTransformField("旋转（度）", transform.RotationDegrees, value => transform with { RotationDegrees = value });
        var keepRatio = new CheckBox
        {
            Content = "保持宽高比",
            IsChecked = _keepAspectRatio
        };
        keepRatio.Click += (_, _) => _keepAspectRatio = keepRatio.IsChecked == true;
        _properties.Children.Add(keepRatio);
        AddTransformField(
            "Scale X",
            transform.ScaleX,
            value => _keepAspectRatio
                ? transform with { ScaleX = value, ScaleY = value }
                : transform with { ScaleX = value },
            0.01d);
        AddTransformField(
            "Scale Y",
            transform.ScaleY,
            value => _keepAspectRatio
                ? transform with { ScaleX = value, ScaleY = value }
                : transform with { ScaleY = value },
            0.01d);

        _properties.Children.Add(CreateLayerSlider(
            "明度",
            layer.Brightness * 100d,
            0d,
            200d,
            async value => await _session.EditManyAsync(
                _selectedLayerIds,
                state => state with { Brightness = value / 100d })));
        _properties.Children.Add(CreateLayerSlider(
            "不透明度",
            layer.Opacity * 100d,
            0d,
            100d,
            async value => await _session.EditManyAsync(
                _selectedLayerIds,
                state => state with { Opacity = value / 100d })));

        var switches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        var visible = new CheckBox { Content = "可见（持久状态）", IsChecked = layer.IsVisible };
        visible.Click += async (_, _) =>
        {
            if (visible.IsChecked != true
                && _isolatedLayerId is { } isolated
                && _selectedLayerIds.Contains(isolated))
                SetIsolatedLayer(null);
            await _session.EditManyAsync(
                _selectedLayerIds,
                state => state with { IsVisible = visible.IsChecked == true });
        };
        switches.Children.Add(visible);
        var locked = new CheckBox { Content = "锁定", IsChecked = layer.IsLocked };
        locked.Click += async (_, _) =>
            await _session.EditManyAsync(_selectedLayerIds, state => state with { IsLocked = locked.IsChecked == true });
        switches.Children.Add(locked);
        _properties.Children.Add(switches);

        var automatic = new Button
        {
            Content = layer.ManualTransformOverride is null ? "当前使用自动对齐" : "恢复自动对齐",
            IsEnabled = layer.ManualTransformOverride is not null,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        automatic.Click += async (_, _) =>
            await _session.EditAsync(layer.LayerId, state => state with { ManualTransform = null });
        _properties.Children.Add(automatic);
        var rootLayer = new Button
        {
            Content = "将此层设为楼层基准",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        rootLayer.Click += async (_, _) => await _session.SetFloorRootAsync(layer.LayerId);
        _properties.Children.Add(rootLayer);
        _updating = false;

        void AddTransformField(
            string label,
            double value,
            Func<double, SurveyLayerTransform> update,
            double minimum = -100000d)
        {
            var field = CreateNumberField(label, value, minimum, 100000d);
            field.ValueChanged += async (_, args) =>
            {
                if (_updating || !double.IsFinite(args.NewValue))
                    return;
                var changed = update(args.NewValue);
                if (changed.IsValid)
                    await _session.EditAsync(layer.LayerId, state => state with { ManualTransform = changed });
            };
            _properties.Children.Add(field);
        }
    }

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
