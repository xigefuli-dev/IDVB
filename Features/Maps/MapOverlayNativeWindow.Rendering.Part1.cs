using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;
internal static partial class MapOverlayBitmapRenderer
{

    internal static Bitmap GetOrLoadScaledMapImage(
        string imagePath,
        int width,
        int height,
        uint dpi)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        // A scale can drift by fractions of a pixel on every alignment. Keep
        // only the latest raster for each source image/DPI instead of retaining
        // one full-size bitmap for every observed dimension.
        var key = $"{Path.GetFullPath(imagePath)}|dpi={dpi}";
        lock (ImageCacheLock)
        {
            if (ScaledImageCache.TryGetValue(key, out var cached))
            {
                if (cached.Width == width && cached.Height == height)
                    return cached.Bitmap;
                cached.Bitmap.Dispose();
                ScaledImageCache.Remove(key);
            }

            var source = GetOrLoadMapImage(imagePath);
            var scaled = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            scaled.SetResolution(
                dpi == 0 ? DefaultDpi : dpi,
                dpi == 0 ? DefaultDpi : dpi);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            }
            ScaledImageCache[key] = new ScaledImageCacheEntry(
                width,
                height,
                scaled);
            return scaled;
        }
    }

    private static SizeF MeasureWrapped(Graphics graphics, string text, Font font, float width)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.None,
            FormatFlags = StringFormatFlags.LineLimit
        };
        return graphics.MeasureString(text, font, new SizeF(width, 10000f), format);
    }

    private static float MeasureUnwrappedWidth(Graphics graphics, string text, Font font) =>
        Math.Max(1f, graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width);

    private static Font CreateFont(float pixelSize, FontStyle style) =>
        new("Segoe UI", Math.Max(1f, pixelSize), style, GraphicsUnit.Pixel);

    private static float ScaleFor(uint dpi) => Math.Max(1f, (dpi == 0 ? DefaultDpi : dpi) / DefaultDpi);

    private static int ScaleAlpha(int sourceAlpha, int opacityByte) =>
        Math.Clamp(sourceAlpha * opacityByte / 255, 0, 255);

    private static GraphicsPath CreateRoundedRectangle(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static Color AnchorColor(string key) => key switch
    {
        "main-entrance" => Color.FromArgb(255, 38, 133, 255),
        "side-entrance" => Color.FromArgb(255, 63, 207, 123),
        "second-floor-primary" => Color.FromArgb(255, 132, 94, 247),
        _ => Color.FromArgb(255, 236, 150, 61)
    };

    internal static Color AnnotationColor(int colorIndex) => colorIndex switch
    {
        0 => Color.FromArgb(255, 255, 59, 48),
        1 => Color.FromArgb(255, 255, 149, 0),
        2 => Color.FromArgb(255, 255, 204, 0),
        3 => Color.FromArgb(255, 52, 199, 89),
        4 => Color.FromArgb(255, 50, 173, 230),
        5 => Color.FromArgb(255, 0, 122, 255),
        6 => Color.FromArgb(255, 175, 82, 222),
        7 => Color.FromArgb(255, 255, 45, 85),
        _ => Color.FromArgb(255, 242, 242, 242)
    };

    internal static Color AnnotationColor(string? colorHex, int fallbackIndex)
    {
        if (!MapAnnotationColor.TryNormalize(colorHex, out var normalized))
            return AnnotationColor(fallbackIndex);
        return Color.FromArgb(
            255,
            Convert.ToInt32(normalized.Substring(1, 2), 16),
            Convert.ToInt32(normalized.Substring(3, 2), 16),
            Convert.ToInt32(normalized.Substring(5, 2), 16));
    }

    internal static Color StatusColor(MapOverlayStatusLevel level) => level switch
    {
        MapOverlayStatusLevel.Success => Color.FromArgb(255, 91, 220, 138),
        MapOverlayStatusLevel.Warning => Color.FromArgb(255, 255, 184, 77),
        MapOverlayStatusLevel.Failure => Color.FromArgb(255, 255, 105, 97),
        MapOverlayStatusLevel.Scanning => Color.FromArgb(255, 91, 176, 255),
        MapOverlayStatusLevel.ManualSelection => Color.FromArgb(255, 115, 193, 255),
        _ => Color.FromArgb(255, 225, 225, 225)
    };
}
