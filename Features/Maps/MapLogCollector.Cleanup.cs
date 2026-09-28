namespace IDVBuff.Features.Maps;

public sealed partial class MapLogCollector
{
    private static void ThrowIfCleanupFailed(IReadOnlyList<string> failures)
    {
        if (failures.Count == 0)
            return;

        throw new IOException(
            "日志收集已关闭，但部分日志文件未能删除："
            + string.Join("；", failures));
    }
}
