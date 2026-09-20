namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 测绘工程状态变化。字段为宿主 <c>SurveyStatusSnapshot</c> 的镜像，
/// 枚举以字符串表达，消息契约与宿主内部类型解耦。
/// </summary>
public sealed record SurveyStatusChangedMessage(
    string? ProjectId,
    string? ProjectName,
    string? ProjectState,
    string RuntimeState,
    string? FloorKey,
    int ObservationCount,
    int RegisteredCount,
    int UnregisteredCount,
    int DeletedCount,
    int PendingCount,
    DateTimeOffset? LastCaptureAt,
    long Revision,
    bool IsSaving,
    string LastErrorCode,
    string? LastMessage,
    string? DiagnosticId);
