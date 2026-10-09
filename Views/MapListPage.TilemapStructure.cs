using IDVBuff.Features.Maps;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using OpenCvSharp;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private async Task ConfigureTilemapFloorStructureAsync(
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
            if (entry.SharedStructure is not null)
            {
                await ShowMessageAsync("该层已使用共享结构", "换小抄请用“重新对准小抄”；换成另一张真实地图请重新制作。");
                return;
            }
            var path = await PickImageAsync("选择该层底图文件夹中的 rendered-base.png");
            if (path is null || !IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            if (!string.Equals(Path.GetFileName(path), "rendered-base.png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择完整楼层文件夹中的 rendered-base.png，旁边需要保留 source.json。");
            var directory = Path.GetDirectoryName(path)!;
            var source = await MapTilemapFloorSource.ReadAsync(directory, cancellationToken);
            if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            var registration = await MapArtworkRegistrationDialog.ShowAsync(
                XamlRoot, artworkPath, path, entry.ArtworkRegistration, cancellationToken, artworkProfile);
            if (registration is null || !IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            var algorithmPath = draft?.PrebuiltStructureAlgorithmPath;
            if (string.IsNullOrWhiteSpace(algorithmPath) || !File.Exists(algorithmPath))
            {
                var catalog = await _repository.GetCatalogSnapshotAsync();
                if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
                algorithmPath = catalog.Maps.Where(map => map.Class == draft?.Class)
                    .SelectMany(map => map.Floors.Where(floor => floor.PrebuiltStructureLine?.IsCurrent is true)
                        .Select(floor => _repository.GetPrebuiltStructureAlgorithmPath(map, floor.Key)))
                    .FirstOrDefault(File.Exists);
            }
            if (algorithmPath is null)
            {
                var picker = new FileOpenPicker(((App)Application.Current).MainWindow.AppWindow.Id)
                {
                    CommitButtonText = "选择制作算法", ViewMode = PickerViewMode.List
                };
                picker.FileTypeFilter.Add(".idva");
                algorithmPath = (await picker.PickSingleFileAsync())?.Path;
            }
            if (string.IsNullOrWhiteSpace(algorithmPath) || !IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            var previewPath = Path.Combine(Path.GetTempPath(), "idvb-source-preview-" + Guid.NewGuid().ToString("N") + ".png");
            using (var artwork = Cv2.ImDecode(File.ReadAllBytes(artworkPath), ImreadModes.Unchanged))
            using (var transparent = MapBackgroundProcessor.CreateWhiteKeyOverlay(artwork))
            using (var baked = MapArtworkRegistrationService.Bake(transparent, registration))
                if (!Cv2.ImWrite(previewPath, baked)) throw new IOException("无法生成对准预览。");
            entry.StructureReferenceMapId = null;
            entry.StructureReferenceFloorKey = string.Empty;
            entry.StructureSourceDirectory = directory;
            entry.StructureSourceAlgorithmPath = algorithmPath;
            entry.ArtworkRegistration = registration;
            entry.ArtworkCropProfile = new FloorRecognitionProfile
            {
                RecognitionRegion = registration.SourceCropRegion?.Clone(),
                FreeCropPoints = registration.SourceCropPoints.Select(point => point.Clone()).ToList()
            };
            entry.PreviewImagePath = previewPath;
            onChanged();
            await ShowMessageAsync("本层结构底图已选定",
                $"源地图 {source.LayoutId} · {source.FloorKey}。继续制作时会生成并保存本层结构，小抄单独保存；源中的未绘制片段记录会随地图包保留。");
        }
        catch (Exception ex)
        {
            if (!IsCurrentImportFloor(entry, cancellationToken, artworkPath, floorKey)) return;
            await ShowMessageAsync("无法使用外部底图", ex.Message);
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
