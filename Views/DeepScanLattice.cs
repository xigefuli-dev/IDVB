using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace IDVBuff.Views;

/// <summary>Independent GPU cells, with a right-to-left carrier and seeded local variation.</summary>
internal sealed class DeepScanLattice : UserControl
{
    private readonly List<(SpriteVisual Visual, float Floor, float Range, int Seed)> _cells = [];
    private ContainerVisual? _root;
    private CompositionPropertySet? _flow;
    private InsetClip? _fillClip;
    private double _fillBoundary;
    private bool _running;
    private CompositionPropertySet? _selectionGeometry;

    public DeepScanLattice()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Rebuild();
        SizeChanged += (_, _) => Rebuild();
        Unloaded += (_, _) => Release();
    }

    internal void SetRunning(bool running)
    {
        if (_running == running) return;
        _running = running;
        UpdateMotion();
    }

    internal void SetFillBoundary(double leadingEdge)
    {
        _fillBoundary = leadingEdge;
        if (_fillClip is not null)
            _fillClip.RightInset = (float)ScanModeVisualRules.FillRightInset(ActualWidth, leadingEdge);
    }

    internal void BindFillBoundary(CompositionPropertySet geometry)
    {
        _selectionGeometry = geometry;
        if (_fillClip is null) return;
        using var boundary = geometry.Compositor.CreateExpressionAnimation("pose.Width - pose.SelectionX");
        boundary.SetReferenceParameter("pose", geometry);
        _fillClip.StartAnimation("RightInset", boundary);
    }

    private void Release()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _root?.Children.RemoveAll();
        foreach (var cell in _cells)
        {
            cell.Visual.Brush.Dispose();
            cell.Visual.Dispose();
        }
        _cells.Clear();
        _root?.Dispose();
        _root = null;
        _fillClip?.Dispose();
        _fillClip = null;
        _flow?.Dispose();
        _flow = null;
    }

    private void Rebuild()
    {
        if (!IsLoaded || ActualWidth < 20 || ActualHeight < 20) return;
        Release();
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _root = compositor.CreateContainerVisual();
        _root.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _fillClip = compositor.CreateInsetClip();
        _root.Clip = _fillClip;
        SetFillBoundary(_fillBoundary);
        if (_selectionGeometry is not null) BindFillBoundary(_selectionGeometry);
        _flow = compositor.CreatePropertySet();
        _flow.InsertScalar("Phase", 0);
        var columns = Math.Max(2, (int)((ActualWidth - 20) / 5));
        for (var x = 0; x < columns; x++)
        for (var y = 0; y < 8; y++)
        {
            var spec = ScanModeVisualRules.LatticeCell(x, y, columns);
            if (!spec.Visible) continue;
            var p = spec.Progress;
            var seed = spec.Seed;
            // Sparse left edge, broken mid-field, dense hot core. Local contrast
            // is wide enough to sparkle, while the left/right envelopes stay apart.
            var floor = spec.Floor;
            var range = spec.Range;
            var cell = compositor.CreateSpriteVisual();
            cell.Size = new Vector2(spec.Size);
            cell.Offset = new Vector3(10 + x * 5 + (3.2f - spec.Size) / 2,
                (float)(ActualHeight - 38) / 2 + y * 5 + (3.2f - spec.Size) / 2, 0);
            cell.Brush = compositor.CreateColorBrush(Color.FromArgb(255,
                (byte)(139 + 107 * p), (byte)(73 + 159 * p), (byte)(215 + 40 * p)));
            cell.Properties.InsertScalar("Noise", .5f);
            cell.Properties.InsertScalar("Position", p * 10 + y * .24f);
            cell.Properties.InsertScalar("Floor", floor);
            cell.Properties.InsertScalar("Range", range);
            _root.Children.InsertAtTop(cell);
            _cells.Add((cell, floor, range, seed));
        }
        ElementCompositionPreview.SetElementChildVisual(this, _root);
        UpdateMotion();
    }

    private void UpdateMotion()
    {
        if (_flow is null || _root is null) return;
        var compositor = _root.Compositor;
        using var linear = compositor.CreateLinearEasingFunction();
        using var shimmerEase = compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1));
        _flow.StopAnimation("Phase");
        if (_running)
        {
            using var carrier = compositor.CreateScalarKeyFrameAnimation();
            carrier.InsertKeyFrame(0, 0);
            carrier.InsertKeyFrame(1, MathF.Tau, linear);
            carrier.Duration = TimeSpan.FromMilliseconds(3400);
            carrier.IterationBehavior = AnimationIterationBehavior.Forever;
            _flow.StartAnimation("Phase", carrier);
        }
        foreach (var (visual, floor, range, seed) in _cells)
        {
            visual.StopAnimation("Opacity");
            visual.Properties.StopAnimation("Noise");
            visual.Opacity = floor + range * .5f;
            if (!_running) continue;
            using var noise = compositor.CreateScalarKeyFrameAnimation();
            var first = seed % 101 / 100f;
            noise.InsertKeyFrame(0, first);
            for (var k = 1; k < 8; k++)
                noise.InsertKeyFrame(k / 8f, ((seed / (k + 1) + k * 37) % 101) / 100f, shimmerEase);
            noise.InsertKeyFrame(1, first, shimmerEase);
            noise.Duration = TimeSpan.FromMilliseconds(700 + seed % 1100);
            noise.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.Properties.StartAnimation("Noise", noise);
            using var opacity = compositor.CreateExpressionAnimation(
                "cell.Floor + cell.Range * (0.14 * (Sin(flow.Phase + cell.Position) + 1) / 2 + 0.86 * cell.Noise * cell.Noise)");
            opacity.SetReferenceParameter("flow", _flow);
            opacity.SetReferenceParameter("cell", visual.Properties);
            visual.StartAnimation("Opacity", opacity);
        }
    }
}
