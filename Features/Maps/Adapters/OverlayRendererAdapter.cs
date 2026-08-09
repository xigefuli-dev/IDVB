using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Maps.Adapters;

/// <summary>IOverlayRenderer 适配器 — 委托给 MapOverlayBitmapRenderer 静态方法。</summary>
public sealed class OverlayRendererAdapter : IOverlayRenderer
{
    public object Render(object scene) =>
        MapOverlayBitmapRenderer.Render((MapOverlayRenderScene)scene);

    public object ComposeDynamic(object lockedBackground, object scene) =>
        MapOverlayBitmapRenderer.ComposeDynamic(
            (System.Drawing.Bitmap)lockedBackground,
            (MapOverlayRenderScene)scene);

    public void InvalidateImageCache() =>
        MapOverlayBitmapRenderer.InvalidateImageCache();
}
