namespace IDVBuff.Features.Maps;

public sealed partial class MapCvRecognitionService
{
    internal bool IsVpsg3Ready(MapRecord map, string floorKey)
    {
        if (_disposed
            || MapAlignmentChannelRegistry.Resolve(map, floorKey).Channel
                == MapAlignmentChannel.LowStructure
            || !TryGetVpsg3IndexKey(map, floorKey, out var key)
            || !_vpsg3Registry.TryGet(key, out var lease))
        {
            return false;
        }

        lease.Dispose();
        return true;
    }

}
