namespace IDVBuff.PluginHostMessages;

/// <summary>
/// 地图对齐锁定。transform 字段为宿主 <c>MapSimilarityTransform</c> 的镜像。
/// </summary>
public sealed record MapLockedMessage(
    string? MapId,
    string? Floor,
    double Scale,
    double RotationDegrees,
    double TranslationX,
    double TranslationY,
    double Confidence);
