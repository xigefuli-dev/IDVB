// IDVB Remaster Phase 0.3 — Core Contract

namespace IDVBuff.Core.Contracts;

// TODO: Phase 0.4 — 替换为 Core/Models 中的实际类型
// using IDVBuff.Core.Models;

/// <summary>
/// 楼层指示器识别抽象。定位 1F/2F 数字对并通过亮度和纹理对比
/// 判断当前激活的楼层按钮。
/// </summary>
public interface IFloorRecognizer : IDisposable
{
    /// <summary>
    /// 从 BGRA 像素缓冲区识别当前楼层。
    /// </summary>
    /// <param name="bgraPixels">BGRA 格式的像素数据。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <param name="stride">每行字节数。</param>
    /// <returns>分类结果，包含楼层标识和置信度。</returns>
    object /* FloorIndicatorClassification */ Recognize(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride);

    /// <summary>
    /// 从 BGRA 像素缓冲区识别当前楼层，带调优参数。
    /// </summary>
    object /* FloorIndicatorClassification */ Recognize(
        ReadOnlySpan<byte> bgraPixels,
        int width,
        int height,
        int stride,
        object /* MapFloorRecognitionTuning */ tuning);

    /// <summary>
    /// 从图像矩阵识别当前楼层（默认调优参数）。
    /// </summary>
    object /* FloorIndicatorClassification */ Recognize(object /* Mat */ image);

    /// <summary>
    /// 从图像矩阵识别当前楼层，带调优参数。
    /// </summary>
    object /* FloorIndicatorClassification */ Recognize(
        object /* Mat */ image,
        object /* MapFloorRecognitionTuning */ tuning);
}
