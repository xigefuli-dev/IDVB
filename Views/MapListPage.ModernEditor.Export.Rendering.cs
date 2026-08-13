using IDVBuff.Features.Maps;
using Windows.UI;
using SD = System.Drawing;
using SD2D = System.Drawing.Drawing2D;
using SDImaging = System.Drawing.Imaging;
using SDText = System.Drawing.Text;

namespace IDVBuff.Views;

public sealed partial class MapListPage : UserControl
{
    private static byte[] RenderModernPngExport(
        FloorRecognitionProfile profile,
        string imagePath,
        ModernPngExportOptions options,
        Func<string, string, bool> isVisible)
    {
        using var source = new SD.Bitmap(imagePath);
        if (source.Width <= 0 || source.Height <= 0)
            throw new InvalidOperationException("无法解码地图原图。");
        var region = profile.GetEffectiveRecognitionRegion();
        var crop = ModernPngPixelRegionOf(region, source.Width, source.Height);
        var scaleX = options.Width / (double)crop.Width;
        var scaleY = options.Height / (double)crop.Height;
        var scale = Math.Min(scaleX, scaleY);

        using var output = new SD.Bitmap(options.Width, options.Height, SDImaging.PixelFormat.Format32bppArgb);
        using (var graphics = SD.Graphics.FromImage(output))
        {
            graphics.SmoothingMode = SD2D.SmoothingMode.AntiAlias;
            graphics.InterpolationMode = SD2D.InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = SDText.TextRenderingHint.AntiAlias;
            graphics.Clear(ToSystemDrawingColor(options.BackgroundColor));

            if (isVisible("image", "image"))
            {
                var sourceRect = new SD.Rectangle(crop.Left, crop.Top, crop.Width, crop.Height);
                graphics.DrawImage(
                    source,
                    new SD.Rectangle(0, 0, options.Width, options.Height),
                    sourceRect,
                    SD.GraphicsUnit.Pixel);
            }

            graphics.SetClip(new SD.RectangleF(0, 0, options.Width, options.Height));

            if (isVisible("graphics", string.Empty))
            {
                foreach (var annotation in profile.Annotations)
                {
                    if (!annotation.IsValid || !isVisible("graphics", ModernAnnotationKey(annotation.Id)))
                        continue;
                    DrawModernPngAnnotation(graphics, annotation, region, options.Width, options.Height, scale);
                }
            }

            if (profile.RecognitionRegion?.IsValid is true && isVisible("special", "crop"))
            {
                using var pen = CreateModernPngPen(RecognitionRegionRed, 2.5 * scale, dashed: true);
                graphics.DrawRectangle(pen, 0, 0, options.Width - 1, options.Height - 1);
            }

            if (isVisible("special", string.Empty))
            {
                foreach (var anchor in profile.Anchors)
                {
                    if (anchor.Bounds?.IsValid is not true || !isVisible("special", ModernAnchorKey(anchor.Id)))
                        continue;
                    DrawModernPngAnchor(graphics, anchor, options.Width, options.Height, scale);
                }
            }
        }

        var pixels = ModernPngToBgraBytes(output);
        return EncodeModernPng(pixels, options.Width, options.Height, options.CompressionLevel);
    }

    private static ModernPngPixelRegion ModernPngPixelRegionOf(NormalizedRectangle region, int width, int height)
    {
        var left = Math.Clamp((int)Math.Floor(region.X * width), 0, Math.Max(0, width - 1));
        var top = Math.Clamp((int)Math.Floor(region.Y * height), 0, Math.Max(0, height - 1));
        var right = Math.Clamp(
            (int)Math.Ceiling((region.X + region.Width) * width),
            left + 1,
            width);
        var bottom = Math.Clamp(
            (int)Math.Ceiling((region.Y + region.Height) * height),
            top + 1,
            height);
        return new ModernPngPixelRegion(left, top, right - left, bottom - top);
    }

    private static void DrawModernPngAnnotation(
        SD.Graphics graphics,
        MapAnnotation annotation,
        NormalizedRectangle region,
        int outWidth,
        int outHeight,
        double scale)
    {
        if (annotation.Type == MapAnnotationType.Line
            && annotation.Start is not null && annotation.End is not null)
        {
            var x1 = (float)((annotation.Start.X - region.X) / region.Width * outWidth);
            var y1 = (float)((annotation.Start.Y - region.Y) / region.Height * outHeight);
            var x2 = (float)((annotation.End.X - region.X) / region.Width * outWidth);
            var y2 = (float)((annotation.End.Y - region.Y) / region.Height * outHeight);
            using var pen = CreateModernPngPen(annotation.EffectiveColorHex, 3d * scale, dashed: false);
            pen.StartCap = SD2D.LineCap.Round;
            pen.EndCap = SD2D.LineCap.Round;
            graphics.DrawLine(pen, x1, y1, x2, y2);
            return;
        }
        if (annotation.Bounds?.IsValid is not true)
            return;
        var rect = ModernPngSourceToOutputRect(annotation.Bounds, region, outWidth, outHeight);
        if (!ModernPngIntersects(rect, outWidth, outHeight))
            return;
        if (annotation.Type == MapAnnotationType.Outline)
        {
            using var pen = CreateModernPngPen(annotation.EffectiveColorHex, 2.5 * scale, dashed: false);
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            return;
        }
        DrawModernPngText(graphics, annotation, rect, scale);
    }

    private static SD.RectangleF ModernPngSourceToOutputRect(
        NormalizedRectangle source,
        NormalizedRectangle region,
        int outWidth,
        int outHeight) => new(
        (float)((source.X - region.X) / region.Width * outWidth),
        (float)((source.Y - region.Y) / region.Height * outHeight),
        (float)(source.Width / region.Width * outWidth),
        (float)(source.Height / region.Height * outHeight));

    private static bool ModernPngIntersects(SD.RectangleF rect, int outWidth, int outHeight) =>
        rect.Right > 0 && rect.Bottom > 0 && rect.X < outWidth && rect.Y < outHeight;

    private static void DrawModernPngText(
        SD.Graphics graphics,
        MapAnnotation annotation,
        SD.RectangleF rect,
        double scale)
    {
        var text = annotation.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text) || rect.Width <= 0 || rect.Height <= 0)
            return;
        var fitting = CalculateFittingFontSize(text, rect.Width, rect.Height);
        var requested = (annotation.FontSize ?? fitting) * scale;
        var fontSize = Math.Clamp(Math.Min(requested, fitting), 1, 512);

        var legacyStyle = annotation.FontFamily is null && annotation.FontSize is null
            && annotation.IsBold is null && annotation.IsItalic is null && annotation.IsStrikethrough is null;
        var style = SD.FontStyle.Regular;
        if (legacyStyle || annotation.IsBold is true)
            style |= SD.FontStyle.Bold;
        if (annotation.IsItalic is true)
            style |= SD.FontStyle.Italic;

        var familyName = string.IsNullOrWhiteSpace(annotation.FontFamily)
            ? "Microsoft YaHei UI"
            : annotation.FontFamily;
        using var family = CreateModernPngFontFamily(familyName);
        using var font = new SD.Font(family, (float)fontSize, style, SD.GraphicsUnit.Pixel);
        using var brush = new SD.SolidBrush(ToSystemDrawingColor(annotation.EffectiveColorHex));
        using var format = new SD.StringFormat
        {
            Alignment = SD.StringAlignment.Center,
            LineAlignment = SD.StringAlignment.Center,
            Trimming = SD.StringTrimming.None,
            FormatFlags = SD.StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, rect, format);
        if (annotation.IsStrikethrough is true)
        {
            var thickness = Math.Max(1d, fontSize / 14d);
            using var linePen = new SD.Pen(brush.Color, (float)thickness);
            var midY = rect.Y + rect.Height / 2f;
            graphics.DrawLine(linePen, rect.X, midY, rect.X + rect.Width, midY);
        }
    }

    private static SD.FontFamily CreateModernPngFontFamily(string familyName)
    {
        try
        {
            return new SD.FontFamily(familyName);
        }
        catch (ArgumentException)
        {
            // GenericSansSerif 是共享实例，不能直接返回并被调用方释放；
            // 按其族名新建独立实例。
            return new SD.FontFamily(SD.FontFamily.GenericSansSerif.Name);
        }
    }

    private static void DrawModernPngAnchor(
        SD.Graphics graphics,
        RecognitionAnchor anchor,
        int outWidth,
        int outHeight,
        double scale)
    {
        if (anchor.Bounds?.IsValid is not true)
            return;
        var rect = new SD.RectangleF(
            (float)(anchor.Bounds.X * outWidth),
            (float)(anchor.Bounds.Y * outHeight),
            (float)(anchor.Bounds.Width * outWidth),
            (float)(anchor.Bounds.Height * outHeight));
        if (!ModernPngIntersects(rect, outWidth, outHeight))
            return;
        using var pen = CreateModernPngPen(GetAnchorColor(anchor), 3.5 * scale, dashed: false);
        graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static SD.Pen CreateModernPngPen(Color color, double thickness, bool dashed) =>
        CreateModernPngPen(ToSystemDrawingColor(color), thickness, dashed);

    private static SD.Pen CreateModernPngPen(string colorHex, double thickness, bool dashed) =>
        CreateModernPngPen(ToSystemDrawingColor(colorHex), thickness, dashed);

    private static SD.Pen CreateModernPngPen(SD.Color color, double thickness, bool dashed)
    {
        var pen = new SD.Pen(color, (float)Math.Max(.5, thickness));
        if (dashed)
            pen.DashStyle = SD2D.DashStyle.Dash;
        return pen;
    }

    private static SD.Color ToSystemDrawingColor(Color color) =>
        SD.Color.FromArgb(color.A, color.R, color.G, color.B);

    private static SD.Color ToSystemDrawingColor(string colorHex)
    {
        if (!MapAnnotationColor.TryNormalize(colorHex, out var normalized))
            normalized = MapAnnotationColor.Default;
        return SD.Color.FromArgb(
            255,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private static byte[] ModernPngToBgraBytes(SD.Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var rect = new SD.Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, SDImaging.ImageLockMode.ReadOnly, SDImaging.PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[width * height * 4];
            for (var row = 0; row < height; row++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    new IntPtr(data.Scan0.ToInt64() + (row * data.Stride)),
                    bytes,
                    row * width * 4,
                    width * 4);
            }
            return bytes;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
