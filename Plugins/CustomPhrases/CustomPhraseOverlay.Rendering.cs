using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace IDVBuff.Plugins.CustomPhrases;

internal sealed partial class CustomPhraseOverlay
{
    private void Render()
    {
        // Cursor polling and WM_INPUT share the same drawing surface.
        lock (_renderGate)
            RenderCore();
    }

    private void RenderCore()
    {
        PhraseBox[] boxes;
        Rectangle windowBounds;
        int selected;
        lock (_sync)
        {
            if (!_visible || _handle == IntPtr.Zero)
                return;
            boxes = _boxes.ToArray();
            windowBounds = _windowBounds;
            selected = _selectedIndex;
        }

        using var bitmap = CreateMenuBitmap(boxes, windowBounds, selected);
        UpdateLayeredBitmap(bitmap, windowBounds);
    }

    private static Bitmap CreateMenuBitmap(
        IReadOnlyList<PhraseBox> boxes,
        Rectangle windowBounds,
        int selected)
    {
        var bitmap = new Bitmap(
            Math.Max(1, windowBounds.Width),
            Math.Max(1, windowBounds.Height),
            PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            using var font = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var surface = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            using (var shadow = new LinearGradientBrush(surface,
                Color.FromArgb(176, 48, 50, 55),
                Color.FromArgb(228, 7, 9, 13), LinearGradientMode.Vertical))
            {
                graphics.FillRectangle(shadow, surface);
            }

            foreach (var box in boxes)
            {
                var local = new Rectangle(
                    box.Bounds.X - windowBounds.X,
                    box.Bounds.Y - windowBounds.Y,
                    box.Bounds.Width,
                    box.Bounds.Height);
                var isSelected = box.Index == selected;
                using var background = new LinearGradientBrush(local,
                    isSelected ? Color.FromArgb(248, 119, 73, 31) : Color.FromArgb(224, 48, 52, 59),
                    isSelected ? Color.FromArgb(252, 59, 37, 26) : Color.FromArgb(240, 13, 16, 22),
                    LinearGradientMode.Vertical);
                using var border = new Pen(
                    isSelected ? Color.FromArgb(255, 255, 202, 95) : Color.FromArgb(200, 112, 121, 135),
                    isSelected ? 4f : 1.5f);
                graphics.FillRectangle(background, local);
                // Keep the thicker selected border wholly inside the bitmap.
                var borderBounds = RectangleF.Inflate(local, -2f, -2f);
                graphics.DrawRectangle(border, borderBounds.X, borderBounds.Y,
                    borderBounds.Width, borderBounds.Height);
                using var textBrush = new SolidBrush(
                    isSelected ? Color.FromArgb(255, 255, 248, 226) : Color.FromArgb(255, 231, 235, 242));
                var horizontalPadding = local.Width * 0.10f;
                var textBounds = new RectangleF(
                    local.X + horizontalPadding,
                    local.Y,
                    local.Width - (horizontalPadding * 2f),
                    local.Height);
                graphics.DrawString(CustomPhrasePluginData.ToDisplayText(box.Phrase),
                    font, textBrush, textBounds, format);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
