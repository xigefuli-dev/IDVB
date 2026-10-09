using IDVBuff.Features.Maps;

namespace IDVBuff.Views;

public sealed partial class MapListPage
{
    private CancellationTokenSource? _importWorkflowCancellation;

    private static FloorRecognitionProfile? GetImportArtworkCropProfile(MapDraft draft, string floorKey)
    {
        var floor = draft.Floors.FirstOrDefault(item => item.Key == floorKey);
        if (floor?.SharedStructure is null) return draft.Recognition.GetFloor(floorKey)?.Clone();
        var registration = floor.ArtworkRegistration;
        return registration is null ? null : new FloorRecognitionProfile
        {
            RecognitionRegion = registration.SourceCropRegion?.Clone(),
            FreeCropPoints = registration.SourceCropPoints.Select(point => point.Clone()).ToList()
        };
    }

    private void CancelImportWorkflow()
    {
        var cancellation = _importWorkflowCancellation;
        _importWorkflowCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private bool IsCurrentImportWorkflow(MapDraft draft, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested && ReferenceEquals(_draft, draft)
        && _importWorkflowCancellation?.Token == cancellationToken;

    private bool IsCurrentImportFloor(ImportFloorEntry entry, CancellationToken cancellationToken,
        string artworkPath, string floorKey) =>
        !cancellationToken.IsCancellationRequested
        && _importWorkflowCancellation?.Token == cancellationToken
        && _pendingImportFloors?.Contains(entry) is true
        && entry.ImagePath == artworkPath && entry.FloorKey == floorKey;

    private async Task CommitImportFloorsWithStructuresAsync(MapDraft draft,
        IReadOnlyList<ImportFloorEntry> entries, CancellationToken cancellationToken)
    {
        // Preparing several floors must not leave half-converted coordinates
        // in the editor when a later floor fails and the maker retries.
        var floors = new MapRecord { Floors = draft.Floors }.Clone().Floors;
        var recognition = draft.Recognition.Clone();
        var artworkPaths = new Dictionary<string, string>(draft.FloorPaths);
        var previewPaths = new Dictionary<string, string>(draft.FloorPreviewPaths);
        var canonicalPaths = new Dictionary<string, string>(draft.FloorRecognitionSourcePaths);
        var binaryPaths = new Dictionary<string, string>(draft.PrebuiltStructureLinePaths);
        var sidePaths = new Dictionary<string, string>(draft.SideEntranceFeaturePaths);
        var sourceProfiles = draft.SharedStructureSourceProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        var gates = draft.PortableGates.Select(gate => gate.Clone()).ToList();
        var algorithmPath = draft.PrebuiltStructureAlgorithmPath;
        var firstPath = draft.FloorOnePath;
        var secondPath = draft.FloorTwoPath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitPendingFloorsToDraft(draft, entries);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.StructureReferenceMapId is { } referenceId && entry.ArtworkRegistration is { } registration)
                    await _repository.ReuseFloorStructureAsync(draft, entry.FloorKey, referenceId,
                        entry.StructureReferenceFloorKey, registration, cancellationToken);
                else if (entry.StructureSourceDirectory.Length > 0 && entry.ArtworkRegistration is { } sourceRegistration)
                    await _repository.PrepareTilemapFloorStructureAsync(draft, entry.FloorKey,
                        entry.StructureSourceDirectory, entry.StructureSourceAlgorithmPath, sourceRegistration,
                        cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            draft.Floors = floors;
            draft.Recognition = recognition;
            draft.FloorPaths = artworkPaths;
            draft.FloorPreviewPaths = previewPaths;
            draft.FloorRecognitionSourcePaths = canonicalPaths;
            draft.PrebuiltStructureLinePaths = binaryPaths;
            draft.SideEntranceFeaturePaths = sidePaths;
            draft.SharedStructureSourceProfiles = sourceProfiles;
            draft.PortableGates = gates;
            draft.PrebuiltStructureAlgorithmPath = algorithmPath;
            draft.FloorOnePath = firstPath;
            draft.FloorTwoPath = secondPath;
            throw;
        }
    }
}
