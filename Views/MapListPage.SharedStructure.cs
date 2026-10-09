using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenCvSharp;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private async Task ConfigureImportFloorStructureAsync(
        ImportFloorEntry entry, Action onChanged, Button confirmButton)
    {
        var cancellationToken = _importWorkflowCancellation?.Token ?? new CancellationToken(true);
        var artworkPath = entry.ImagePath;
        var floorKey = entry.FloorKey;
        if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
        var draft = _draft!;
        var artworkProfile = entry.ArtworkCropProfile?.Clone();
        var floorArea = _importFloorScrollViewer;
        CancelPendingImportClick();
        ResetImportFloorDragSession(animateReturn: false);
        if (floorArea is not null) floorArea.IsEnabled = false;
        confirmButton.IsEnabled = false;
        try
        {
            var catalog = await _repository.GetCatalogSnapshotAsync();
            if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            var references = catalog.Maps.SelectMany(map => map.Floors
                .Where(floor => floor.PrebuiltStructureLine?.IsCurrent is true
                    && File.Exists(_repository.GetFloorRecognitionPath(map, floor.Key)))
                .Select(floor => (Map: map, Floor: floor))).ToArray();
            if (references.Length == 0)
            {
                await ShowMessageAsync("暂无结构底图", "请先为一张地图制作结构资源，再复用到同一地图的小抄。");
                return;
            }
            var picker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = references.Select(item => $"{item.Map.Class} · {item.Map.DisplayName} · {item.Floor.DisplayName}").ToArray(),
                SelectedIndex = 0 };
            var currentIndex = Array.FindIndex(references, item =>
                item.Map.Id == (entry.StructureReferenceMapId ?? draft?.Id)
                && item.Floor.Key == (entry.StructureReferenceFloorKey.Length > 0
                    ? entry.StructureReferenceFloorKey : entry.OriginalFloorKey));
            if (currentIndex >= 0) picker.SelectedIndex = currentIndex;
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = "选择同一真实地图、同一楼层的结构参照，再将这张小抄对准它。其他楼层不会代替本层。",
                TextWrapping = TextWrapping.Wrap });
            content.Children.Add(picker);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "复用结构底图",
                Content = content, PrimaryButtonText = "对准小抄", CloseButtonText = "取消" };
            if (await dialog.ShowThemedAsync(this) != ContentDialogResult.Primary)
                return;
            if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            var selected = references[picker.SelectedIndex];
            var registration = await MapArtworkRegistrationDialog.ShowAsync(XamlRoot, artworkPath,
                _repository.GetFloorRecognitionPath(selected.Map, selected.Floor.Key), entry.ArtworkRegistration,
                cancellationToken, artworkProfile);
            if (registration is null || !IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            // Preview is disposable maker output, not a catalog or recognition source.
            var previewPath = Path.Combine(Path.GetTempPath(), $"idvb-artwork-preview-{Guid.NewGuid():N}.png");
            await Task.Run(() =>
            {
                using var original = Cv2.ImDecode(File.ReadAllBytes(artworkPath), ImreadModes.Unchanged);
                using var transparent = MapBackgroundProcessor.CreateWhiteKeyOverlay(original);
                using var baked = MapArtworkRegistrationService.Bake(transparent, registration);
                if (!Cv2.ImWrite(previewPath, baked))
                    throw new InvalidOperationException("无法生成小抄配准预览。");
            }, cancellationToken);
            if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            entry.StructureReferenceMapId = selected.Map.Id;
            entry.StructureReferenceFloorKey = selected.Floor.Key;
            entry.StructureSourceDirectory = string.Empty;
            entry.StructureSourceAlgorithmPath = string.Empty;
            entry.SharedStructure = selected.Floor.SharedStructure?.Clone() ?? new MapSharedFloorStructure
            {
                UpdatedAt = selected.Map.UpdatedAt
            };
            entry.ArtworkRegistration = registration;
            entry.ArtworkCropProfile = new FloorRecognitionProfile
            {
                RecognitionRegion = registration.SourceCropRegion?.Clone(),
                FreeCropPoints = registration.SourceCropPoints.Select(point => point.Clone()).ToList()
            };
            entry.PreviewImagePath = previewPath;
            onChanged();
        }
        catch (Exception ex)
        {
            if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            await ShowMessageAsync("无法复用结构底图", ex.Message);
        }
        finally
        {
            if (IsCurrentImportWorkflow(draft, cancellationToken))
            {
                if (floorArea is not null) floorArea.IsEnabled = true;
                confirmButton.IsEnabled = CanCommitImportFloors();
            }
        }
    }
}
