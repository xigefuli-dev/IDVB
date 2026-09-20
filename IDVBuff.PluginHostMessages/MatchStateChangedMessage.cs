namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 比赛状态变化。字段为宿主 <c>MapMatchSnapshot</c> 的镜像，
/// 枚举以字符串表达，消息契约与宿主内部类型解耦。
/// </summary>
public sealed record MatchStateChangedMessage(
    string State,
    string? PlayerSlot,
    int Version,
    string? MapClass,
    string? MatchId,
    string Mode,
    string? SurveyProjectId,
    string? FloorKey);
