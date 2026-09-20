using System.Drawing;
using System.Drawing.Drawing2D;

namespace IDVBuff.Features.Maps;

internal static partial class MapOverlayBitmapRenderer
{
    private static void DrawMiniMapPlayers(
        Graphics graphics,
        MapOverlayRenderMap miniMap,
        RectangleF destRect,
        float dpiScale,
        IReadOnlyList<MiniMapTrackedPlayer>? players,
        float? rotationDegrees)
    {
        if (players is null || players.Count == 0) return;

        var markerDiameter = Math.Clamp(destRect.Width * 0.13f, 14f * dpiScale, 30f * dpiScale);
        var half = markerDiameter * 0.5f;

        var oldInterpolation = graphics.InterpolationMode;
        var oldQuality = graphics.CompositingQuality;
        var oldSmoothing = graphics.SmoothingMode;
        try
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            foreach (var player in players)
            {
                if (player.NormalizedX < 0 || player.NormalizedX > 1
                    || player.NormalizedY < 0 || player.NormalizedY > 1)
                    continue;

                var px = destRect.Left + (float)(player.NormalizedX * destRect.Width);
                var py = destRect.Top + (float)(player.NormalizedY * destRect.Height);

                var iconPath = MapPlayerAssetCatalog.ResolvePath(player.Slot);
                if (!File.Exists(iconPath)) continue;

                Bitmap icon;
                lock (ImageCacheLock)
                {
                    icon = GetOrLoadMapImage(iconPath);
                }

                var playerState = graphics.Save();
                try
                {
                    graphics.TranslateTransform(px, py);
                    if (rotationDegrees is { } rot && float.IsFinite(rot))
                    {
                        graphics.RotateTransform(-rot);
                    }
                    graphics.DrawImage(
                        icon,
                        new RectangleF(-half, -half, markerDiameter, markerDiameter));
                }
                finally
                {
                    graphics.Restore(playerState);
                }
            }
        }
        finally
        {
            graphics.InterpolationMode = oldInterpolation;
            graphics.CompositingQuality = oldQuality;
            graphics.SmoothingMode = oldSmoothing;
        }
    }
}
