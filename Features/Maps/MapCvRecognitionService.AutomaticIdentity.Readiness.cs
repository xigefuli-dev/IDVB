namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    private static List<AutomaticIdentityFloorInput> BuildAutomaticIdentityFloorInputs(
        IReadOnlyList<MapRecord> mapSnapshot,
        string? requestedFloorKey)
    {
        var floorInputs = new List<AutomaticIdentityFloorInput>();
        foreach (var map in mapSnapshot)
        {
            var orderedFloors = MapFloorRules.GetOrderedFloors(map);
            if (requestedFloorKey is { } explicitFloor)
            {
                var resolved = MapScanFloorRules.ResolveFloorKey(map, explicitFloor);
                floorInputs.Add(resolved is null
                    ? new AutomaticIdentityFloorInput(
                        map,
                        explicitFloor,
                        HasFloorDefinition: false,
                        FailureReason: "所选楼层在该地图中不存在。")
                    : new AutomaticIdentityFloorInput(
                        map,
                        resolved,
                        HasFloorDefinition: true,
                        FailureReason: string.Empty));
                continue;
            }

            if (orderedFloors.Count == 0)
            {
                floorInputs.Add(new AutomaticIdentityFloorInput(
                    map,
                    MapFloorRules.GetPrimaryFloorKey(map),
                    HasFloorDefinition: false,
                    FailureReason: "地图没有可枚举的楼层定义。"));
                continue;
            }

            floorInputs.AddRange(orderedFloors.Select(floor =>
                new AutomaticIdentityFloorInput(
                    map,
                    floor.Key,
                    HasFloorDefinition: true,
                    FailureReason: string.Empty)));
        }
        return floorInputs;
    }

    private MapAutomaticIdentityAttempt? PrepareAutomaticIdentityFloorLeases(
        AutomaticIdentityFloorWork[] work,
        AutomaticIdentityRun run,
        CancellationToken cancellationToken,
        List<Vpsg3FloorIndexLease> leases)
    {
        var readinessPending = false;
        foreach (var item in work)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                item.FailureReason = "预检期间操作已取消。";
                return run.Finish(MapAutomaticIdentityStatus.Cancelled, 0,
                    "自动识别已取消。");
            }

            if (MapNoDoorAlignmentBudgetContext.RemainingMilliseconds is <= 0)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.TimedOut;
                item.FailureReason = "地图开图身份识别超过时间预算。";
                return run.Finish(MapAutomaticIdentityStatus.TimedOut, 0,
                    "自动识别超过时间预算，请下次开图重试。");
            }

            if (!item.HasFloorDefinition)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.MissingFloor;
                item.BlocksAcceptance = true;
                item.FailureReason = item.InitialFailureReason;
                readinessPending = true;
                continue;
            }

            var profile = MapFloorRules.GetFloorProfile(item.Map, item.FloorKey);
            if (profile is null)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                item.BlocksAcceptance = true;
                item.FailureReason = "楼层识别配置尚未准备好。";
                readinessPending = true;
                continue;
            }

            if (MapAlignmentChannelRegistry.Resolve(item.Map, item.FloorKey).Channel
                == MapAlignmentChannel.LowStructure)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.Unsupported;
                item.BlocksAcceptance = true;
                item.FailureReason = "低结构楼层不支持 VPSG3 自动身份比较。";
                readinessPending = true;
                continue;
            }

            try
            {
                if (!TryGetVpsg3FloorLease(item.Map, item.FloorKey, out var lease)
                    || lease is null)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                    item.BlocksAcceptance = true;
                    item.FailureReason =
                        "该地图楼层的 VPSG3 预制结构索引缺失或仍在构建。";
                    readinessPending = true;
                    continue;
                }

                item.Lease = lease;
                leases.Add(lease);
                item.Status = MapAutomaticIdentityCandidateStatus.Ready;
            }
            catch (Exception exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    item.Status = MapAutomaticIdentityCandidateStatus.Cancelled;
                    item.FailureReason = "索引预检期间操作已取消。";
                    return run.Finish(MapAutomaticIdentityStatus.Cancelled, 0,
                        "自动识别已取消。");
                }
                item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                item.BlocksAcceptance = true;
                item.FailureReason =
                    $"楼层索引预检失败：{exception.GetType().Name}: {exception.Message}";
                readinessPending = true;
            }
        }

        if (readinessPending)
        {
            return run.Finish(MapAutomaticIdentityStatus.ResourcesPending, 0,
                "候选地图楼层索引尚未完整就绪，未执行局部赢家比较。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return run.Finish(MapAutomaticIdentityStatus.Cancelled, 0,
                "自动识别已取消。");
        }

        return null;
    }

    private MapAutomaticIdentityAttempt? ValidateAutomaticIdentityPool(
        AutomaticIdentityFloorWork[] work,
        AutomaticIdentityRun run,
        MapCatalogRevision snapshotRevision,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return run.Finish(MapAutomaticIdentityStatus.Cancelled, 0,
                "自动识别已取消。");
        }

        if (!_cacheInitialized
            || snapshotRevision == MapCatalogRevision.Empty
            || snapshotRevision != _catalogRevision
            || _repository.GetCatalogRevision() != snapshotRevision)
        {
            foreach (var item in work)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                item.FailureReason = "地图目录缓存尚未与当前资源同步。";
            }
            return run.Finish(MapAutomaticIdentityStatus.ResourcesPending, 0,
                "地图目录或楼层索引尚未就绪，请重试。");
        }

        return work.Length == 0
            ? run.Finish(MapAutomaticIdentityStatus.InsufficientEvidence, 0,
                "当前地图类别没有可比较的地图楼层。")
            : null;
    }

    private static bool TryExtractAutomaticIdentityObservation(
        CapturedGameFrame frame,
        AutomaticIdentityFloorWork[] work,
        AutomaticIdentityRun run,
        CancellationToken cancellationToken,
        out Vpsg3LiveObservation observation,
        out MapAutomaticIdentityAttempt? failure)
    {
        try
        {
            observation = frame.GetOrCreateVpsg3Observation();
            failure = null;
            return true;
        }
        catch (Exception exception)
        {
            observation = null!;
            if (cancellationToken.IsCancellationRequested)
            {
                failure = run.Finish(MapAutomaticIdentityStatus.Cancelled, 0,
                    "自动识别已取消。");
                return false;
            }

            foreach (var item in work)
            {
                item.Status = MapAutomaticIdentityCandidateStatus.ResourcesPending;
                item.BlocksAcceptance = true;
                item.FailureReason =
                    $"当前帧 VPSG3 特征提取失败：{exception.GetType().Name}: {exception.Message}";
            }
            failure = run.Finish(MapAutomaticIdentityStatus.ResourcesPending, 0,
                "当前帧的 VPSG3 特征暂不可用。");
            return false;
        }
    }
}
