using IDVBuff.Features.Maps;
using IDVBuff.PluginHostMessages;
using IDVBuff.Survey.Domain;

namespace IDVBuff.Features.Plugins;

/// <summary>
/// 宿主快照 → 消息 DTO 的纯静态映射函数。无 WindowsAppSDK 依赖，
/// 因此可被 <c>IDVBuff.Tests</c> 通过 Compile Include 直接链接测试。
/// 枚举统一以字符串表达，保持消息契约与宿主内部类型解耦。
/// </summary>
public static class HostMessageMapper
{
    public static SessionStateChangedMessage ToSessionStateChanged(
        MapSessionSnapshot session,
        MapMatchSnapshot match,
        string statusMessage,
        bool overlayVisible,
        bool gameMapOpen,
        MapAlignmentTrackingMode alignmentMode) =>
        new(
            session.State.ToString(),
            session.MapId?.ToString(),
            session.Floor,
            session.IsLocked,
            session.Confidence,
            session.StableCandidateFrames,
            session.LocationMethod.ToString(),
            session.RecalibrationReason.ToString(),
            statusMessage,
            overlayVisible,
            gameMapOpen,
            alignmentMode.ToString(),
            session.AlignmentRevision);

    public static MatchStateChangedMessage ToMatchStateChanged(MapMatchSnapshot match) =>
        new(
            match.State.ToString(),
            match.PlayerSlot?.ToString(),
            match.Version,
            match.MapClass,
            match.MatchId == Guid.Empty ? null : match.MatchId.ToString(),
            match.Mode.ToString(),
            match.SurveyProjectId?.ToString(),
            match.FloorKey);

    /// <summary>
    /// 仅在会话已锁定、transform 有效、且 <see cref="MapSessionSnapshot.AlignmentRevision"/>
    /// 相对上次发布发生变化时产出 <see cref="MapLockedMessage"/>，防止锁定后重复的
    /// StateChanged 刷屏。
    /// </summary>
    public static MapLockedMessage? TryToMapLocked(
        MapSessionSnapshot session,
        MapSimilarityTransform? lockedTransform,
        ref long lastPublishedLockedRevision)
    {
        if (!session.IsLocked
            || lockedTransform?.IsValid is not true
            || session.AlignmentRevision == lastPublishedLockedRevision)
        {
            return null;
        }

        lastPublishedLockedRevision = session.AlignmentRevision;
        return new MapLockedMessage(
            session.MapId?.ToString(),
            session.Floor,
            lockedTransform.Scale,
            lockedTransform.RotationDegrees,
            lockedTransform.TranslationX,
            lockedTransform.TranslationY,
            session.Confidence);
    }

    public static HotkeyInvokedMessage ToHotkeyInvoked(PluginHotkeyKind kind) => new(kind);

    public static SurveyStatusChangedMessage ToSurveyStatusChanged(
        SurveyStatusSnapshot snapshot) =>
        new(
            snapshot.ProjectId?.ToString(),
            snapshot.ProjectName,
            snapshot.ProjectState?.ToString(),
            snapshot.RuntimeState.ToString(),
            snapshot.FloorKey,
            snapshot.ObservationCount,
            snapshot.RegisteredCount,
            snapshot.UnregisteredCount,
            snapshot.DeletedCount,
            snapshot.PendingCount,
            snapshot.LastCaptureAt,
            snapshot.Revision,
            snapshot.IsSaving,
            snapshot.LastErrorCode.ToString(),
            snapshot.LastMessage,
            snapshot.DiagnosticId);
}
