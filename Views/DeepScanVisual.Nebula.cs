using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace IDVBuff.Views;

internal sealed partial class DeepScanVisual
{
    // A stable procedural field: resizing or returning to the page never rolls
    // a new cloud. Separate plates drift on the compositor at different speeds.
    private static float Noise(int seed)
    {
        var value = unchecked((uint)seed * 747796405u + 2891336453u);
        value = ((value >> (int)((value >> 28) + 4)) ^ value) * 277803737u;
        return ((value >> 22) ^ value) / (float)uint.MaxValue;
    }

    private static void DrawNebula(CanvasDrawingSession ds, ICanvasResourceCreator device, float w, float h)
    {
        var center = new Vector2(w * .51f, h * .51f);
        Glow(ds, device, center, w * .43f, h * .40f, Color.FromArgb(140, 95, 29, 232));
        Glow(ds, device, new Vector2(w * .65f, h * .34f), w * .26f, h * .26f,
            Color.FromArgb(160, 187, 47, 255));
        Glow(ds, device, new Vector2(w * .36f, h * .66f), w * .24f, h * .22f,
            Color.FromArgb(110, 79, 67, 244));
        // Overlapping knots break up the old smooth elliptical halo. Each arm
        // has its own warped radius, color temperature, and turbulent density.
        for (var arm = 0; arm < 3; arm++)
        for (var i = 0; i < 84; i++)
        {
            var seed = 271 + arm * 907 + i * 31;
            var theta = i / 84f * MathF.Tau + arm * .65f;
            var warp = MathF.Sin(theta * 3 + arm) * .025f + (Noise(seed) - .5f) * .05f;
            var radius = .27f + arm * .037f + warp;
            var point = center + new Vector2(MathF.Cos(theta) * w * radius,
                MathF.Sin(theta) * h * (radius - .018f));
            var size = 11 + Noise(seed + 1) * 35;
            var alpha = (byte)(17 + Noise(seed + 2) * 38);
            var color = arm switch
            {
                0 => Color.FromArgb(alpha, 219, 115, 255),
                1 => Color.FromArgb(alpha, 157, 54, 246),
                _ => Color.FromArgb(alpha, 93, 76, 244)
            };
            Glow(ds, device, point, size * 1.35f, size * .72f, color);
            if (i % 3 == 0)
                Glow(ds, device, point, size * .45f, size * .22f, Ice((byte)(alpha + 15)));
        }
        // A visible hot knot above the right edge of the card, with a long lens
        // streak and violet backscatter below it, rather than a hidden center.
        var core = new Vector2(w * .69f, h * .285f);
        Glow(ds, device, core, 98, 51, Purple(170));
        Glow(ds, device, core, 42, 21, Color.FromArgb(225, 214, 155, 255));
        Glow(ds, device, core, 16, 8, Ice(255));
        Glow(ds, device, core, w * .22f, 3.2f, Ice(210));
        Glow(ds, device, new Vector2(w * .42f, h * .735f), w * .22f, 10, Purple(175));
    }

    private static void DrawWisps(CanvasDrawingSession ds, ICanvasResourceCreator device, float w, float h)
    {
        var center = new Vector2(w * .51f, h * .51f);
        for (var strand = 0; strand < 17; strand++)
        {
            using var path = new CanvasPathBuilder(device);
            for (var step = 0; step <= 100; step++)
            {
                var theta = step / 100f * 4.5f + strand * .53f;
                var flutter = .010f * MathF.Sin(theta * 9 + strand * 2)
                    + .004f * MathF.Sin(theta * 21 + strand);
                var radius = .267f + strand % 5 * .016f + flutter;
                var point = center + new Vector2(MathF.Cos(theta) * w * radius,
                    MathF.Sin(theta) * h * (radius - .008f));
                point.X += MathF.Sin(theta * 2 + strand) * 11;
                if (step == 0) path.BeginFigure(point);
                else path.AddLine(point);
            }
            path.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(path);
            using var filament = new CanvasLinearGradientBrush(device,
                Color.FromArgb(0, 123, 56, 255), Ice((byte)(40 + strand % 4 * 16)))
            {
                StartPoint = new Vector2(w * .15f, h * .65f),
                EndPoint = new Vector2(w * .76f, h * .26f)
            };
            ds.DrawGeometry(geometry, Purple(20), 6);
            ds.DrawGeometry(geometry, filament, strand % 4 == 0 ? 1.25f : .55f);
        }
        var core = new Vector2(w * .69f, h * .285f);
        for (var i = 0; i < 48; i++)
        {
            var angle = Noise(i * 43 + 2) * MathF.Tau;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle) * .47f);
            var length = 28 + Noise(i * 13 + 7) * 125;
            using var brush = new CanvasLinearGradientBrush(device, Ice((byte)(25 + i % 5 * 13)), Purple(0))
            {
                StartPoint = core, EndPoint = core + direction * length
            };
            ds.DrawLine(core + direction * 10, core + direction * length, brush, i % 7 == 0 ? 1.2f : .5f);
        }
    }

    private static void DrawDust(CanvasDrawingSession ds, ICanvasResourceCreator device, float w, float h)
    {
        for (var i = 0; i < 210; i++)
        {
            var theta = Noise(i * 47 + 31) * MathF.Tau;
            var radius = .27f + Noise(i * 29 + 71) * .14f;
            var x = w * (.51f + MathF.Cos(theta) * radius);
            var y = h * (.51f + MathF.Sin(theta) * radius);
            var alpha = (byte)(35 + Noise(i * 11 + 47) * 150);
            var size = .35f + Noise(i * 53 + 9) * 1.1f;
            ds.FillCircle(x, y, size, Ice(alpha));
            if (i % 19 != 0) continue;
            Glow(ds, device, new Vector2(x, y), 8, 5, Purple(100));
            ds.DrawLine(x - 3.5f, y, x + 3.5f, y, Ice(150), .6f);
            ds.DrawLine(x, y - 3, x, y + 3, Ice(115), .6f);
        }
    }
}
