using System.Diagnostics;
using OpenCvSharp;

namespace IDVBuff.Features.Maps;

internal sealed record MapEntryIdentityDecision(MapRecord? Map, string FloorKey, string Reason,
    bool CompetitionComplete, double Confidence, double ElapsedMilliseconds, IReadOnlyList<object> Evidence)
{
    public MapEntryIdentityPose? SelectedPose { get; init; }
    public string? ValidationPolicy { get; init; }
}

/// <summary>Immutable registered resources; one caller owns current-frame computation and verification.</summary>
internal sealed class MapEntryIdentityIndex : IDisposable
{
    private sealed record Entry(MapRecord Map, FloorDefinition Floor, string Directory, MapEntryIdentityResource Resource);
    private sealed record Pool(Entry[] Entries, bool Complete, string Reason);
    private readonly MapRepository _repository;
    private readonly MapRecord[] _maps;
    private readonly object _gate = new();
    private readonly object _useGate = new();
    private readonly Dictionary<string, Task<Pool>> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid MapId, string FloorKey), MapEntryIdentityResource> _pixels = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public MapEntryIdentityIndex(MapRepository repository, IReadOnlyList<MapRecord> maps)
    { _repository = repository; _maps = maps.Select(m => m.Clone()).ToArray(); }

    public bool HasResources(string? mapClass) => _maps.Any(m =>
        string.Equals(m.Class, mapClass, StringComparison.OrdinalIgnoreCase)
        && MapFloorRules.GetOrderedFloors(m).Any(f => f.EntryIdentityAsset is not null));

    public Task PrepareAsync(string mapClass, string floorKey) => GetPool(mapClass, floorKey);
    public Task PrepareSelectedAsync(string mapClass, string floorKey, Guid mapId) => GetPool(mapClass, floorKey, mapId);
    public bool IsPreparationFinished(string mapClass, string floorKey)
    {
        lock (_gate) return _pools.TryGetValue(Key(mapClass, floorKey), out var task) && task.IsCompleted;
    }
    private static string Key(string mapClass, string floorKey, Guid? mapId = null) =>
        mapClass + "\n" + floorKey + (mapId is null ? string.Empty : "\n" + mapId.Value.ToString());

    internal static bool EntryPassportFallbackAllowed(string reason) => reason is
        "entry-no-reliable-exit" or "entry-passport-unavailable"
        or "entry-coverage-incomplete" or "entry-family-unresolved";

    private Task<Pool> GetPool(string mapClass, string floorKey, Guid? selectedMapId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = Key(mapClass, floorKey, selectedMapId);
            if (!_pools.TryGetValue(key, out var task))
                _pools[key] = task = Task.Run(() => LoadPool(mapClass, floorKey, selectedMapId));
            return task;
        }
    }

    private Pool LoadPool(string mapClass, string floorKey, Guid? selectedMapId)
    {
        var entries = new List<Entry>();
        var complete = true;
        var reason = "entry-resources-ready";
        try
        {
            foreach (var map in _maps.Where(m => string.Equals(m.Class, mapClass, StringComparison.OrdinalIgnoreCase)
                && (!selectedMapId.HasValue || m.Id == selectedMapId.Value)))
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                var floor = MapFloorRules.GetOrderedFloors(map).FirstOrDefault(f =>
                    string.Equals(f.Key, floorKey, StringComparison.OrdinalIgnoreCase));
                if (floor?.EntryIdentityAsset is null) { complete = false; reason = "entry-class-resource-coverage-incomplete"; continue; }
                try
                {
                    var directory = Path.GetDirectoryName(_repository.GetFloorImagePath(map, floor.Key))!;
                    entries.Add(new(map, floor, directory, MapEntryIdentityResources.Load(directory, map, floor)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
                { complete = false; reason = "entry-resource-unavailable: " + ex.Message; }
            }
            _lifetime.Token.ThrowIfCancellationRequested();
            return new(entries.ToArray(), complete && entries.Count > 0, reason);
        }
        catch
        {
            foreach (var entry in entries) entry.Resource.Dispose();
            throw;
        }
    }

    public MapEntryIdentityDecision Identify(CapturedGameFrame frame, string mapClass, string floorKey,
        CancellationToken token, Func<bool> canCompute, Func<IReadOnlyList<GateDetection>> detectGates,
        Guid? selectedMapId = null)
    {
        var watch = Stopwatch.StartNew();
        var evidence = new List<object>();
        MapEntryIdentityDecision Finish(string reason, Entry? entry = null, bool complete = false, double confidence = 0,
            MapEntryIdentityPose? pose = null, string? policy = null) =>
            new(entry?.Map, floorKey, reason, complete, confidence, watch.Elapsed.TotalMilliseconds, evidence)
            { SelectedPose = pose, ValidationPolicy = policy };
        if (!Monitor.TryEnter(_useGate)) return Finish("entry-identification-busy");
        try
        {
            bool CanCompute() => !token.IsCancellationRequested && !_lifetime.IsCancellationRequested && canCompute();
            if (!CanCompute()) return Finish("entry-budget-incomplete");
            var pending = GetPool(mapClass, floorKey, selectedMapId);
            // Selected alignment runs on the alignment worker. Only that map's
            // floor metadata is loaded; unrelated class resources cannot block it.
            if (selectedMapId.HasValue) pending.GetAwaiter().GetResult();
            if (!pending.IsCompletedSuccessfully) return Finish("entry-resources-preparing");
            var pool = pending.Result;
            if (!CanCompute()) return Finish("entry-budget-incomplete");
            if (!pool.Complete) return Finish(pool.Reason);
            var byId = pool.Entries.ToDictionary(e => e.Map.Id.ToString(), StringComparer.Ordinal);
            var passports = pool.Entries.SelectMany(e => e.Resource.Passports).ToArray();
            bool InventoryComplete(Entry entry) => entry.Resource.Coverage.Count > 0
                && entry.Resource.Coverage.All(row => row.TryGetProperty("complete", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.True);
            MapEntryIdentityResource Pixels(Entry entry)
            {
                var key = (entry.Map.Id, entry.Floor.Key);
                if (_pixels.TryGetValue(key, out var resource))
                {
                    _pixels.Remove(key);
                    _pixels.Add(key, resource);
                    return resource;
                }
                // Only immutable author pixels are retained. Each floor owns a
                // separate key; catalog replacement disposes the entire index.
                if (_pixels.Count == 3)
                {
                    var oldest = _pixels.First(); oldest.Value.Dispose(); _pixels.Remove(oldest.Key);
                }
                return _pixels[key] = MapEntryIdentityResources.Load(entry.Directory, entry.Map, entry.Floor);
            }
            using var observation = new MapEntryIdentityFrame(frame.Image, CanCompute);
            if (!CanCompute()) return Finish("entry-budget-incomplete");
            var gates = detectGates();
            if (!CanCompute()) return Finish("entry-budget-incomplete");
            var exits = observation.ObserveExits(gates, frame.ViewportBounds, CanCompute);
            evidence.Add(new { stage = "observation", floorKey, walls = observation.Walls.Count, exits,
                elapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
            var verifiedConfidence = new Dictionary<(string MapId, MapEntryIdentityPose Pose), double>();
            bool Verify(string id, MapEntryIdentityPose pose, bool allWalls, out bool contradicted)
            {
                contradicted = false;
                if (!CanCompute()) return false;
                var entry = byId[id];
                var cross = MapEntryIdentityGeometry.CheckObservedExits(passports, id, floorKey, pose, exits,
                    InventoryComplete(entry), CanCompute);
                contradicted = cross.Contradicted;
                if (!cross.Completed || contradicted) { evidence.Add(new { stage = "exit-verification", id, cross }); return false; }
                var resource = Pixels(entry);
                var author = MapEntryIdentityValidation.VerifyAuthor(observation, resource.AuthorValidationMask, pose, CanCompute);
                var walls = !allWalls || author.Verified && MapEntryIdentityValidation.VerifyWallRaster(observation,
                    resource.CleanMask, resource.KnownBoundary, resource.UnknownMask, pose, CanCompute);
                evidence.Add(new { stage = "current-frame-verification", id, pose, author, walls,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
                if (author.Verified && walls && CanCompute())
                { verifiedConfidence[(id, pose)] = author.WithinSix; return true; }
                return false;
            }
            var coverage = pool.Entries.All(InventoryComplete);
            var fallback = !coverage || passports.Length == 0 || !exits.Any(e => e.IconScore >= .78 || e.DirectionReliable);
            if (!fallback)
            {
                var result = MapEntryIdentityGeometry.Evaluate(passports, exits, observation.Walls, floorKey,
                    observation.IsVisibleFloor, frame.Image.Width, frame.Image.Height, CanCompute,
                    candidate => candidate.LocalGeometryVerified && Verify(candidate.Passport.MapId, candidate.Pose, false, out _));
                evidence.Add(new { stage = "entry-passport", result.Reason, result.Completed, result.FirstVerified,
                    result.Examined, candidates = result.Candidates.Select(c => new { c.Passport.MapId,
                        c.Passport.SourceEntryId, c.Pose, c.HeldOut, planes = c.Planes.Count,
                        rooms = c.Rooms.Count, c.LocalGeometryVerified, c.ExternalVerificationPassed }).ToArray() });
                var selected = result.Candidates.FirstOrDefault(c => c.LocalGeometryVerified && c.ExternalVerificationPassed);
                if (result.FirstVerified && selected is not null && CanCompute())
                    return Finish("first-verified-entry", byId[selected.Passport.MapId], false,
                        verifiedConfidence[(selected.Passport.MapId, selected.Pose)], selected.Pose, "local-fit-and-author");
                fallback = EntryPassportFallbackAllowed(result.Reason);
                if (!fallback) return Finish(result.Reason);
            }
            if (!CanCompute()) return Finish("entry-budget-incomplete");
            var corners = observation.Corners();
            MapEntryCornerVerification VerifyCorner(MapEntryCornerIdentityCandidate candidate)
            {
                var id = candidate.Region.MapId;
                var resource = Pixels(byId[id]);
                MapEntryPartialWallEvidence Refine(bool scale) => MapEntryIdentityValidation.ValidateAndRefine(
                    observation, resource.CleanMask, resource.KnownBoundary, resource.UnknownMask,
                    resource.NativeWalls, candidate.Pose, CanCompute, allowScaleRefinement: scale);
                var geometry = Refine(candidate.EntryKind == "main");
                evidence.Add(new { stage = "partial-wall-geometry", id, geometry,
                    elapsedMilliseconds = watch.Elapsed.TotalMilliseconds });
                if (!geometry.Success) return new(false, geometry.Pose, false, geometry.Reason);
                var success = Verify(id, geometry.Pose, false, out var contradicted);
                // A scale fit is only a proposal. If the original author's pixels
                // reject it, retry the source's independent translation-only fit.
                if (!success && !contradicted && geometry.ScaleRefinement?.Accepted == true && CanCompute())
                {
                    geometry = Refine(false);
                    evidence.Add(new { stage = "partial-wall-translation-retry", id, geometry });
                    success = geometry.Success && Verify(id, geometry.Pose, false, out contradicted);
                }
                return new(success, geometry.Pose, contradicted,
                    success ? "author-and-directed-wall-raster" : "current-frame-verification-failed");
            }
            var main = MapEntryCornerIdentity.RankMain(pool.Entries.SelectMany(e => e.Resource.MainRegions).ToArray(),
                corners.Nodes, floorKey, frame.Image.Width, frame.Image.Height, CanCompute, VerifyCorner);
            evidence.Add(new { stage = "main-entry", main.Reason, main.RetrievalCompleted, main.VerificationComplete,
                candidates = main.Candidates.Select(c => new { c.Region.MapId, c.Pose, c.Score, c.MainChains,
                    c.NeighborChains, c.PoseAmbiguous, c.IdentitySupported, c.Verification }).ToArray() });
            var mainWinner = MapEntryCornerIdentity.SelectMain(main);
            if (mainWinner is not null && CanCompute())
                return Finish(mainWinner.Evidence, byId[mainWinner.Candidate.Region.MapId], true,
                    verifiedConfidence[(mainWinner.Candidate.Region.MapId, mainWinner.Candidate.Verification!.Pose)],
                    mainWinner.Candidate.Verification.Pose, "partial-wall-and-author");
            if (!CanCompute()) return Finish("entry-budget-incomplete");
            var local = MapEntryCornerIdentity.RankLocal(pool.Entries.SelectMany(e => e.Resource.LocalRegions).ToArray(),
                corners.Nodes, corners.Pairs, floorKey, frame.Image.Width, frame.Image.Height, CanCompute, VerifyCorner);
            evidence.Add(new { stage = "local-structure", local.Reason, local.RetrievalCompleted, local.VerificationComplete,
                candidates = local.Candidates.Select(c => new { c.Region.MapId, c.Pose, c.Score,
                    c.ExtentGrid, c.PoseAmbiguous, c.IdentitySupported, c.Verification }).ToArray() });
            var localWinner = MapEntryCornerIdentity.SelectLocal(local, floorKey, main.Candidates,
                frame.Image.Width, frame.Image.Height);
            return localWinner is not null && CanCompute()
                ? Finish(localWinner.Evidence, byId[localWinner.Candidate.Region.MapId], true,
                    verifiedConfidence[(localWinner.Candidate.Region.MapId, localWinner.Candidate.Verification!.Pose)],
                    localWinner.IdentityOnly ? null : localWinner.Candidate.Verification.Pose, "partial-wall-and-author")
                : Finish(local.Reason);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            evidence.Add(new { stage = "resource-unavailable", error = ex.Message });
            return Finish("entry-resource-unavailable");
        }
        finally
        {
            Monitor.Exit(_useGate);
        }
    }

    public void Dispose()
    {
        Task<Pool>[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); pending = _pools.Values.ToArray();
        }
        try { Task.WaitAll(pending); } catch (AggregateException) { }
        lock (_useGate)
        {
            foreach (var task in pending.Where(t => t.IsCompletedSuccessfully))
                foreach (var entry in task.Result.Entries) entry.Resource.Dispose();
            foreach (var resource in _pixels.Values) resource.Dispose();
            _pixels.Clear();
        }
        _lifetime.Dispose();
    }
}
