using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace IDVBuff.Views;

public sealed partial class HomePage
{
    private Action<bool> CreateAccountLoadingOutline(Grid slot)
    {
        var overlay = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        slot.Children.Add(overlay);
        var compositor = ElementCompositionPreview.GetElementVisual(overlay).Compositor;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(1, 1);
        geometry.Size = new Vector2(56, 56);
        geometry.CornerRadius = new Vector2(8, 8);
        geometry.TrimStart = 0;
        geometry.TrimEnd = 0.24f;
        var stroke = compositor.CreateColorBrush();
        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = stroke;
        shape.StrokeThickness = 2;
        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(58, 58);
        visual.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(overlay, visual);
        var travel = compositor.CreateScalarKeyFrameAnimation();
        travel.InsertKeyFrame(0, 0);
        travel.InsertKeyFrame(1, 1, compositor.CreateLinearEasingFunction());
        travel.Duration = TimeSpan.FromSeconds(1.2);
        travel.IterationBehavior = AnimationIterationBehavior.Forever;
        return loading =>
        {
            geometry.StopAnimation("TrimOffset");
            overlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (loading)
            {
                stroke.Color = (FluentTheme.Brush(this, "AccentFillColorDefaultBrush") as SolidColorBrush)?.Color
                    ?? Windows.UI.Color.FromArgb(255, 45, 150, 255);
                geometry.TrimOffset = 0;
                geometry.StartAnimation("TrimOffset", travel);
            }
        };
    }
}
