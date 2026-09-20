// IDVB Remaster — 后台扫描（Background Scan）开图消费
// 玩家第一次打开游戏地图时，消费后台扫描保存的识别结果：
// 候选（如有）→ 缩放（如有）→ 尝试一次对齐，然后按标准仅对齐流程提交。

using IDVBuff.Core.Models;
using IDVBuff.Pipeline;

namespace IDVBuff.Features.Maps;
public sealed partial class SessionOrchestrator
{

    private static string ResolveBackgroundConsumeFloorKey(
        RuntimeMapRecognition locked)
    {
        var floorKey = locked.Result.Floor;
        if (MapFloorRules.GetFloorProfile(locked.Map, floorKey) is null)
            floorKey = MapScanFloorRules.ResolveScanFloorKey(locked.Map);
        return floorKey;
    }

    private static MapAlignmentSession CreateIndependentFloorSeedSession(
        RuntimeMapRecognition locked,
        string floorKey)
    {
        var transform = MapFloorScaleSeedRules.CreateIndependentFloorSeed(
            locked.Map,
            floorKey);
        return new MapAlignmentSession
        {
            MapId = locked.Map.Id,
            MapUpdatedAt = locked.Map.UpdatedAt,
            FloorKey = floorKey,
            LockedTransform = transform,
            BaselineGateScale = transform.ScaleX,
            HasGatePairLock = false,
            Mode = MapAlignmentTrackingMode.GatePairLocked,
            SideEntranceScanPriorConfidence = 0d
        };
    }
}
