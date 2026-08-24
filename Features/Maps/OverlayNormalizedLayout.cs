using System.Drawing;

namespace IDVBuff.Features.Maps;

/// <summary>
/// Resolves overlay parts from normalized 0..1 positions. The returned
/// rectangles are always inside the viewport and never overlap. Collisions
/// are resolved vertically so each part keeps its requested X position.
/// </summary>
internal static class OverlayNormalizedLayout
{
    internal sealed record Result(RectangleF? Status, RectangleF? MiniMap);

    public static Result Resolve(
        SizeF viewport,
        SizeF? statusSize,
        PointF statusPosition,
        SizeF? miniMapSize,
        PointF miniMapPosition,
        float gap)
    {
        if (viewport.Width <= 0f || viewport.Height <= 0f)
            return new Result(null, null);

        var status = statusSize is { } requestedStatus
            ? Place(Fit(requestedStatus, viewport), statusPosition, viewport)
            : (RectangleF?)null;
        var miniMap = miniMapSize is { } requestedMiniMap
            ? Place(Fit(requestedMiniMap, viewport), miniMapPosition, viewport)
            : (RectangleF?)null;

        if (status is null || miniMap is null || !Intersects(status.Value, miniMap.Value))
            return new Result(status, miniMap);

        return ResolveVerticalCollision(
            status.Value,
            miniMap.Value,
            Clamp01(statusPosition.Y) <= Clamp01(miniMapPosition.Y),
            viewport,
            Math.Max(0f, gap));
    }

    private static Result ResolveVerticalCollision(
        RectangleF status,
        RectangleF miniMap,
        bool statusIsUpper,
        SizeF viewport,
        float gap)
    {
        if (status.Height + miniMap.Height + gap > viewport.Height)
        {
            // A vertical arrangement is mathematically impossible at the
            // requested sizes. Keep the status readable and aspect-fit only
            // the mini map to the remaining height, anchored to its nearest
            // vertical screen edge.
            var availableHeight = Math.Max(0f, viewport.Height - status.Height - gap);
            var fittedSize = Fit(miniMap.Size, new SizeF(viewport.Width, availableHeight));
            var anchoredToBottom = viewport.Height - miniMap.Bottom < miniMap.Top;
            miniMap.Size = fittedSize;
            miniMap.Y = anchoredToBottom
                ? viewport.Height - fittedSize.Height
                : Math.Clamp(miniMap.Y, 0f, viewport.Height - fittedSize.Height);
            if (miniMap.IsEmpty)
                return new Result(status, miniMap);
        }

        var upper = statusIsUpper ? status : miniMap;
        var lower = statusIsUpper ? miniMap : status;

        var requiredShift = upper.Bottom + gap - lower.Top;
        if (requiredShift <= 0f)
            return new Result(status, miniMap);

        // The lower part pushes the upper part toward the top edge first.
        // If the upper part reaches the edge, the remaining pressure moves
        // the lower part down. X coordinates never participate.
        var moveUpper = Math.Min(requiredShift, upper.Top);
        upper.Y -= moveUpper;
        requiredShift -= moveUpper;

        var moveLower = Math.Min(requiredShift, viewport.Height - lower.Bottom);
        lower.Y += moveLower;
        requiredShift -= moveLower;

        return statusIsUpper
            ? new Result(upper, lower)
            : new Result(lower, upper);
    }

    private static RectangleF Place(SizeF size, PointF normalized, SizeF viewport)
    {
        var x = Clamp01(normalized.X) * Math.Max(0f, viewport.Width - size.Width);
        var y = Clamp01(normalized.Y) * Math.Max(0f, viewport.Height - size.Height);
        return new RectangleF(x, y, size.Width, size.Height);
    }

    private static SizeF Fit(SizeF requested, SizeF available)
    {
        if (requested.Width <= 0f || requested.Height <= 0f
            || available.Width <= 0f || available.Height <= 0f)
        {
            return SizeF.Empty;
        }
        var factor = Math.Min(
            1f,
            Math.Min(available.Width / requested.Width, available.Height / requested.Height));
        return new SizeF(requested.Width * factor, requested.Height * factor);
    }

    private static bool Intersects(RectangleF first, RectangleF second) =>
        first.Left < second.Right
        && first.Right > second.Left
        && first.Top < second.Bottom
        && first.Bottom > second.Top;

    private static float Clamp01(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
