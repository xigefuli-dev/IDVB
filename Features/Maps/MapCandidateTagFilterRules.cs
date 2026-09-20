namespace IDVBuff.Features.Maps;

/// <summary>
/// 纯逻辑规则：在候选界面根据用户选中的标签筛选地图候选结果。
/// </summary>
public static class MapCandidateTagFilterRules
{
    /// <summary>
    /// 根据当前选中的标签字典（groupId -> tagValue）过滤候选列表。
    /// 返回匹配的候选及其在原始列表中的索引。
    /// </summary>
    public static IReadOnlyList<(MapRecognitionChoice Choice, int OriginalIndex)> FilterChoices(
        IReadOnlyList<MapRecognitionChoice> choices,
        IReadOnlyDictionary<Guid, string>? selectedTags)
    {
        ArgumentNullException.ThrowIfNull(choices);

        if (selectedTags is null || selectedTags.Count == 0)
        {
            var all = new List<(MapRecognitionChoice, int)>(choices.Count);
            for (var i = 0; i < choices.Count; i++)
            {
                all.Add((choices[i], i));
            }
            return all;
        }

        var result = new List<(MapRecognitionChoice, int)>();
        for (var i = 0; i < choices.Count; i++)
        {
            var choice = choices[i];
            var map = choice.Recognition.Map;
            var matches = true;

            foreach (var (groupId, expectedTag) in selectedTags)
            {
                if (string.IsNullOrWhiteSpace(expectedTag))
                    continue;

                if (!map.Tags.TryGetValue(groupId, out var actualTag)
                    || !string.Equals(actualTag, expectedTag, StringComparison.OrdinalIgnoreCase))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                result.Add((choice, i));
            }
        }

        return result;
    }

    /// <summary>
    /// 获取指定标签组在目标地图类中可用的标签列表（去重且按字母/自然序排序）。
    /// </summary>
    public static IReadOnlyList<string> ResolveAvailableTags(
        MapTagGroup group,
        string? mapClass,
        IEnumerable<MapRecord>? maps)
    {
        ArgumentNullException.ThrowIfNull(group);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in group.Tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
                set.Add(tag.Trim());
        }

        if (maps is not null && !string.IsNullOrWhiteSpace(mapClass))
        {
            foreach (var map in maps)
            {
                if (string.Equals(map.Class, mapClass, StringComparison.OrdinalIgnoreCase)
                    && map.Tags.TryGetValue(group.Id, out var tagVal)
                    && !string.IsNullOrWhiteSpace(tagVal))
                {
                    set.Add(tagVal.Trim());
                }
            }
        }

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
