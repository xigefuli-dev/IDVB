using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace IDVBuff.Views;

/// <summary>Resolution-independent light plates. Drawn only on load/resize;
/// ambient and impact animation is performed by Composition, never a UI timer.</summary>
internal sealed partial class DeepScanVisual : UserControl
{
    internal enum Layer { Atmosphere, Wisps, Dust, Shock }
    private readonly Layer _layer;
    private CanvasControl? _canvas;

    internal DeepScanVisual(Layer layer)
    {
        _layer = layer;
        IsHitTestVisible = false;
        Loaded += (_, _) => AttachCanvas();
        SizeChanged += (_, _) => _canvas?.Invalidate();
        Unloaded += (_, _) =>
        {
            if (_canvas is not { } canvas) return;
            canvas.Draw -= Draw;
            canvas.RemoveFromVisualTree();
            Content = null;
            _canvas = null;
        };
    }

    private void AttachCanvas()
    {
        if (_canvas is not null) return;
        _canvas = new CanvasControl { IsHitTestVisible = false };
        _canvas.Draw += Draw;
        Content = _canvas;
    }

    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var w = (float)ActualWidth;
        var h = (float)ActualHeight;
        if (w <= 1 || h <= 1) return;
        using var light = new CanvasCommandList(sender);
        using (var ds = light.CreateDrawingSession())
        {
            switch (_layer)
            {
                case Layer.Atmosphere: DrawNebula(ds, sender, w, h); break;
                case Layer.Wisps: DrawWisps(ds, sender, w, h); break;
                case Layer.Dust: DrawDust(ds, sender, w, h); break;
                default: DrawShock(ds, sender, w, h); break;
            }
        }
        using var diffusion = new GaussianBlurEffect
        {
            Source = light, BlurAmount = _layer switch { Layer.Dust => 3, Layer.Wisps => 7, _ => 16 },
            BorderMode = EffectBorderMode.Soft
        };
        using var composite = new CanvasCommandList(sender);
        using (var drawing = composite.CreateDrawingSession())
        {
            drawing.DrawImage(diffusion);
            drawing.DrawImage(light);
        }
        using var mask = new CanvasCommandList(sender);
        using (var drawing = mask.CreateDrawingSession())
        using (var feather = new CanvasRadialGradientBrush(sender,
        [
            new CanvasGradientStop { Position = 0, Color = Ice(255) },
            new CanvasGradientStop { Position = .78f, Color = Ice(255) },
            new CanvasGradientStop { Position = 1, Color = Ice(0) }
        ]))
        {
            feather.Center = new Vector2(w / 2, h / 2);
            feather.RadiusX = w * .49f;
            feather.RadiusY = h * .49f;
            drawing.FillRectangle(0, 0, w, h, feather);
        }
        using var feathered = new AlphaMaskEffect { Source = composite, AlphaMask = mask };
        args.DrawingSession.DrawImage(feathered);
    }

    private static Color Purple(byte a) => Color.FromArgb(a, 162, 62, 255);
    private static Color Ice(byte a) => Color.FromArgb(a, 246, 220, 255);

    private static void Glow(CanvasDrawingSession ds, ICanvasResourceCreator device,
        Vector2 center, float rx, float ry, Color color)
    {
        using var brush = new CanvasRadialGradientBrush(device, color,
            Color.FromArgb(0, color.R, color.G, color.B))
        {
            Center = center, RadiusX = rx, RadiusY = ry
        };
        ds.FillEllipse(center, rx, ry, brush);
    }

    private static void DrawShock(CanvasDrawingSession ds, ICanvasResourceCreator device, float w, float h)
    {
        var core = new Vector2(w * .5f, h * .5f);
        Glow(ds, device, core, w * .40f, h * .38f, Purple(210));
        Glow(ds, device, core, w * .30f, h * .26f, Ice(180));
        ds.DrawEllipse(core, w * .32f, h * .28f, Purple(165), 12);
        ds.DrawEllipse(core, w * .323f, h * .282f, Ice(245), 2.2f);
        ds.DrawEllipse(core, w * .375f, h * .34f, Purple(125), 1.5f);
        Glow(ds, device, core, w * .44f, 8, Ice(245));
        Glow(ds, device, core, w * .12f, h * .29f, Ice(130));
    }

}
