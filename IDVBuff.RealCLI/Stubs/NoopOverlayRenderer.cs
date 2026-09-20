// IDVB Real CLI — 空操作叠加渲染器
// 实现 IOverlayRenderer，不执行实际的 GDI+ 位图渲染。

using IDVBuff.Core.Contracts;

namespace IDVBuff.RealCLI.Stubs;

/// <summary>
/// 空操作叠加渲染器。CLI 不需要像素级叠加输出，
/// 只关心识别结果数据结构。
/// </summary>
public sealed class NoopOverlayRenderer : IOverlayRenderer
{
    public object Render(object scene) => new object(); // 占位符
    public object ComposeDynamic(object lockedBackground, object scene) => new object();
    public void InvalidateImageCache() { }
}
