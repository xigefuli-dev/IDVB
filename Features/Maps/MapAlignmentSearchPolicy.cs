namespace IDVBuff.Features.Maps;

public static class MapAlignmentSearchPolicy
{
    internal static bool UseTrackingForStructureValidation(
        bool isSideEntranceStructureRoute,
        AlignmentSearchContext? searchContext) =>
        searchContext?.UseInitialHighPrecisionRecovery != true
        && (!isSideEntranceStructureRoute
            || searchContext?.UseTrackingStructureSearch == true);

    public static bool UseTrackingForGlobalRecovery(
        AlignmentSearchContext? searchContext) =>
        searchContext?.UseInitialHighPrecisionRecovery != true;
}
/* 中文说明：本文件负责地图对齐搜索策略的规则定义，描述搜索范围、候选约束以及停止条件；它只提供策略数据和判断依据，不直接执行截图、识别或渲染。 */
