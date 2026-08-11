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
    private void ShowLegacyMarkerEditor()
    {
        if (_draft is null || !HasAnyFloorImage(_draft))
            return;
        DetachMarkerHostScroller();
        _draft.Recognition.EnsureStandardAnchors();
        // Rebuilding the editor refreshes the image after a floor click. Keep
        // the selected floor instead of resetting it to the first floor.
        if (!_draft.Floors.Any(floor => floor.Key == _activeFloorKey))
            _activeFloorKey = _draft.Floors.Count > 0 ? _draft.Floors[0].Key : "1f";
        if (GetActiveAnchor() is null)
            _activeAnchorId = null;

        var root = new Grid { Margin = new Thickness(36, 31, 36, 38), MinHeight = 630 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBlock = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Left };
        titleBlock.Children.Add(CreateTitle("特征标记"));
        titleBlock.Children.Add(CreateDescription("第一张图片完成大门和侧门标记后即可确认；其他楼层标记均为可选。"));
        if (IsBatchImport)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量标注：第 {_batchDraftIndex + 1} / {_batchDrafts!.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        else if (IsBatchOperation && _batchQueue is not null)
        {
            titleBlock.Children.Add(new TextBlock
            {
                Text = $"批量{(_batchType == BatchOperationType.Edit ? "编辑" : "导入")}：第 {_batchQueueIndex + 1} / {_batchQueue.Count} 组",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 96, 96, 96))
            });
        }
        header.Children.Add(titleBlock);
        root.Children.Add(header);

        var editor = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        _markerSurface = new Grid
        {
            Height = 540,
            Background = new SolidColorBrush(Color.FromArgb(255, 23, 30, 39)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        var imagePath = GetActiveFloorImagePath();
        var bitmap = CreateBitmap(imagePath!);
        bitmap.ImageOpened += (_, _) =>
        {
            if (bitmap.PixelHeight > 0)
                _imageAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            UpdateMarkerSurfaceHeight();
            RenderMarkerVisuals();
        };
        _markerSurface.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform });
        _markerCanvas = new Canvas { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        _markerSurface.Children.Add(_markerCanvas);
        _markerPanelCanvas = new Canvas();
        _markerControlPanel = CreateMarkerControlPanel();
        _markerControlPanel.SizeChanged += (_, _) => PositionMarkerControlPanel();
        _markerPanelCanvas.Children.Add(_markerControlPanel);
        _markerSurface.Children.Add(_markerPanelCanvas);
        _markerSurface.SizeChanged += (_, _) =>
        {
            UpdateMarkerSurfaceHeight();
            RenderMarkerVisuals();
            PositionMarkerControlPanel();
        };
        _markerSurface.Loaded += (_, _) =>
        {
            AttachMarkerHostScroller();
            PositionMarkerControlPanel();
        };
        _markerSurface.PointerPressed += MarkerSurface_PointerPressed;
        _markerSurface.PointerMoved += MarkerSurface_PointerMoved;
        _markerSurface.PointerReleased += MarkerSurface_PointerReleased;
        _markerSurface.PointerCanceled += MarkerSurface_PointerCanceled;
        editor.Children.Add(_markerSurface);
        Grid.SetRow(editor, 1);
        root.Children.Add(editor);

        _workflowHost.Content = root;
        PlayWorkflowEnterAnimation();
        RenderMarkerVisuals();
        UpdateMarkerConfirmState();
        DispatcherQueue.TryEnqueue(() =>
        {
            AttachMarkerHostScroller();
            PositionMarkerControlPanel();
        });
    }

    private Border CreateMarkerControlPanel()
    {
        var panelLayout = new Grid();
        panelLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panelLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 8),
            Spacing = 5
        };
        var dragHandle = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
            Padding = new Thickness(2, 4, 2, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock
            {
                Text = "楼层",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 220, 224, 230)),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        dragHandle.PointerPressed += MarkerPanelDragHandle_PointerPressed;
        dragHandle.PointerMoved += MarkerPanelDragHandle_PointerMoved;
        dragHandle.PointerReleased += MarkerPanelDragHandle_PointerReleased;
        dragHandle.PointerCanceled += MarkerPanelDragHandle_PointerCanceled;
        header.Children.Add(dragHandle);

        // 动态生成楼层切换按钮（从 draft.Floors）
        // Put floor buttons in a separate grid so additional floors wrap to
        // the next row instead of being squeezed into the header.
        var floorDefinitions = (_draft?.Floors ?? [])
            .OrderBy(floor => floor.SortOrder)
            .ToArray();
        if (floorDefinitions.Length > 0)
        {
            const int buttonsPerRow = 3;
            var floorGrid = new Grid
            {
                ColumnSpacing = 4,
                RowSpacing = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            for (var column = 0; column < buttonsPerRow; column++)
                floorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < (floorDefinitions.Length + buttonsPerRow - 1) / buttonsPerRow; row++)
                floorGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (var index = 0; index < floorDefinitions.Length; index++)
            {
                var floorDef = floorDefinitions[index];
                var label = floorDef.DisplayName.Length > 4
                    ? floorDef.DisplayName[..4]
                    : floorDef.DisplayName;
                var floorButton = CreateFloorButton(label, floorDef.Key);
                Grid.SetColumn(floorButton, index % buttonsPerRow);
                Grid.SetRow(floorButton, index / buttonsPerRow);
                floorGrid.Children.Add(floorButton);
            }
            header.Children.Add(floorGrid);
        }
        panelLayout.Children.Add(header);

        var controls = new StackPanel { Spacing = 7 };

        // 添加标记 切换按钮
        var addMarkerToggle = CreateMarkerPanelButton("添加标记",
            _isAnnotationPanelOpen ? AccentBlue : Color.FromArgb(255, 100, 180, 100));
        if (_isAnnotationPanelOpen)
        {
            addMarkerToggle.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            addMarkerToggle.BorderThickness = new Thickness(2);
        }
        addMarkerToggle.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(addMarkerToggle);
            _isAnnotationPanelOpen = !_isAnnotationPanelOpen;
            _activeAnnotationType = default;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        controls.Children.Add(addMarkerToggle);

        if (_isAnnotationPanelOpen)
        {
            controls.Children.Add(CreateAnnotationSubPanel());
        }

        var regionButton = CreateMarkerPanelButton("区域选择", RecognitionRegionOrange);
        if (_isSelectingRecognitionRegion)
        {
            regionButton.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            regionButton.BorderThickness = new Thickness(2);
        }
        regionButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(regionButton);
            _isSelectingRecognitionRegion = true;
            _activeAnchorId = null;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        controls.Children.Add(regionButton);
        var wholeRegionButton = CreateMarkerPanelButton("整图作为区域", Color.FromArgb(255, 112, 112, 112));
        wholeRegionButton.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(wholeRegionButton);
            ApplyRecognitionRegion(new NormalizedRectangle { Width = 1d, Height = 1d });
            _isSelectingRecognitionRegion = false;
            _activeAnchorId = null;
            _pendingMarker = null;
            _dragStart = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        controls.Children.Add(wholeRegionButton);

        foreach (var requiredAnchor in GetActiveFloorProfile().RequiredAnchors)
            controls.Children.Add(CreateAnchorButton(requiredAnchor));

        var addOptionalButton = CreateMarkerPanelButton("+ 辅助锚点", Color.FromArgb(255, 247, 184, 24));
        addOptionalButton.Click += (_, _) => AddOptionalAnchor();
        controls.Children.Add(addOptionalButton);

        foreach (var optionalAnchor in GetActiveFloorProfile().Anchors
                     .Where(anchor => anchor.Role == RecognitionAnchorRole.Optional))
        {
            if (optionalAnchor.IsBuiltIn)
            {
                controls.Children.Add(CreateAnchorButton(optionalAnchor));
                continue;
            }
            var optionalRow = new Grid { ColumnSpacing = 6 };
            optionalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optionalRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var anchorButton = CreateAnchorButton(optionalAnchor);
            optionalRow.Children.Add(anchorButton);
            var deleteButton = CreateMarkerPanelButton("X", Color.FromArgb(255, 255, 90, 66));
            deleteButton.Padding = new Thickness(0);
            deleteButton.Click += (_, _) => DeleteOptionalAnchor(optionalAnchor.Id);
            Grid.SetColumn(deleteButton, 1);
            optionalRow.Children.Add(deleteButton);
            controls.Children.Add(optionalRow);
        }

        var exitButton = CreateMarkerPanelButton("退出", Color.FromArgb(255, 112, 112, 112));
        exitButton.Click += async (_, _) =>
        {
            ResetBatchOperation();
            await ShowListAsync();
        };
        controls.Children.Add(exitButton);

        _markerConfirmButton = CreateMarkerPanelButton("确认", DisabledGray);
        _markerConfirmButton.Click += async (_, _) =>
        {
            PlayDetailTriggerFeedback(_markerConfirmButton);
            await SaveDraftAsync();
        };
        controls.Children.Add(_markerConfirmButton);

        var scroller = new ScrollViewer
        {
            Content = controls,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroller, 1);
        panelLayout.Children.Add(scroller);
        var panel = new Border
        {
            Width = MarkerPanelPreferredWidth,
            MinWidth = 0,
            Padding = new Thickness(10),
            Background = new SolidColorBrush(Color.FromArgb(218, 16, 24, 34)),
            CornerRadius = new CornerRadius(10),
            Child = panelLayout
        };
        panel.PointerPressed += (_, e) => e.Handled = true;
        return panel;
    }

    private void RefreshMarkerControlPanel()
    {
        if (_markerPanelCanvas is null)
            return;
        _markerPanelCanvas.Children.Clear();
        _markerControlPanel = CreateMarkerControlPanel();
        _markerControlPanel.SizeChanged += (_, _) => PositionMarkerControlPanel();
        _markerPanelCanvas.Children.Add(_markerControlPanel);
        UpdateMarkerConfirmState();
        DispatcherQueue.TryEnqueue(PositionMarkerControlPanel);
    }

    private Button CreateFloorButton(string label, string floorKey)
    {
        var button = CreateMarkerPanelButton(label, Color.FromArgb(255, 129, 129, 129));
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(0);
        if (_activeFloorKey == floorKey)
        {
            button.Background = new SolidColorBrush(AccentBlue);
            button.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            button.BorderBrush = new SolidColorBrush(AccentBlue);
        }
        button.Click += (_, _) =>
        {
            if (_activeFloorKey == floorKey)
                return;
            _activeFloorKey = floorKey;
            _activeAnchorId = null;
            _isSelectingRecognitionRegion = false;
            _activeAnnotationType = default;
            _pendingMarker = null;
            _dragStart = null;
            ShowMarkerEditor();
        };
        return button;
    }

    private Button CreateAnchorButton(RecognitionAnchor anchor)
    {
        var button = CreateMarkerPanelButton(anchor.DisplayName, GetAnchorColor(anchor));
        if (_activeAnchorId == anchor.Id)
        {
            button.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
            button.BorderThickness = new Thickness(2);
        }
        button.Click += (_, _) =>
        {
            PlayDetailTriggerFeedback(button);
            _activeAnchorId = anchor.Id;
            _isSelectingRecognitionRegion = false;
            _pendingMarker = null;
            RefreshMarkerControlPanel();
            RenderMarkerVisuals();
        };
        return button;
    }

    private static Button CreateMarkerPanelButton(string text, Color color)
    {
        var button = new Button
        {
            Content = text,
            Background = new SolidColorBrush(color),
            Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            MinWidth = 0,
            MinHeight = 28,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        AttachHoverFeedback(button);
        return button;
    }

    private void AddOptionalAnchor()
    {
        if (_draft is null)
            return;
        var profile = GetActiveFloorProfile();
        var number = profile.Anchors.Count(anchor => anchor.Role == RecognitionAnchorRole.Optional) + 1;
        var anchor = new RecognitionAnchor
        {
            Key = $"optional-{Guid.NewGuid():N}",
            DisplayName = $"辅助锚点 {number}",
            Role = RecognitionAnchorRole.Optional,
            Weight = 0.35d
        };
        profile.Anchors.Add(anchor);
        _activeAnchorId = anchor.Id;
        _isSelectingRecognitionRegion = false;
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

    private void DeleteOptionalAnchor(Guid anchorId)
    {
        var anchor = GetActiveFloorProfile().FindAnchor(anchorId);
        if (anchor?.Role != RecognitionAnchorRole.Optional || anchor.IsBuiltIn)
            return;
        GetActiveFloorProfile().Anchors.Remove(anchor);
        if (_activeAnchorId == anchor.Id)
            _activeAnchorId = null;
        _pendingMarker = null;
        RefreshMarkerControlPanel();
        RenderMarkerVisuals();
    }

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
