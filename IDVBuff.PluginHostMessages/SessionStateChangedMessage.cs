namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 地图会话状态变化。字段为宿主 <c>MapSessionSnapshot</c> 的镜像，
/// 枚举以字符串表达，消息契约与宿主内部类型解耦。
/// </summary>
public sealed record SessionStateChangedMessage(
    string SessionState,
    string? MapId,
    string? Floor,
    bool IsLocked,
    double Confidence,
    int StableCandidateFrames,
    string LocationMethod,
    string RecalibrationReason,
    string StatusMessage,
    bool OverlayVisible,
    bool GameMapOpen,
    string AlignmentMode,
    long AlignmentRevision);
