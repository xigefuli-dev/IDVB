using System.Runtime.InteropServices;

namespace IDVBuff.Features.Maps;

internal sealed partial class MapEntryIdentityFrame
{
    private int[]? _visibleIntegral;
    private int[]? _brownIntegral;
    private int _integralStride;

    private void PrepareExitColorIntegrals()
    {
        if (_visibleIntegral is not null) return;
        var width = Floor.Width;
        var height = Floor.Height;
        var colorStride = (int)_bgr.Step();
        var maskStride = (int)Floor.Step();
        var colors = new byte[height * colorStride];
        var visible = new byte[height * maskStride];
        Marshal.Copy(_bgr.Data, colors, 0, colors.Length);
        Marshal.Copy(Floor.Data, visible, 0, visible.Length);
        _integralStride = width + 1;
        _visibleIntegral = new int[(height + 1) * _integralStride];
        _brownIntegral = new int[_visibleIntegral.Length];
        for (var y = 0; y < height; y++)
        {
            var validCount = 0;
            var brownCount = 0;
            for (var x = 0; x < width; x++)
            {
                if (visible[y * maskStride + x] != 0)
                {
                    validCount++;
                    var pixel = y * colorStride + x * 3;
                    var b = colors[pixel]; var g = colors[pixel + 1]; var r = colors[pixel + 2];
                    var mean = (b + g + r) / 3d;
                    var chroma = Math.Max(Math.Max(b, g), r) - Math.Min(Math.Min(b, g), r);
                    if (r >= b + 6 && r >= g - 7 && mean is >= 55 and <= 215 && chroma <= 105)
                        brownCount++;
                }
                var i = (y + 1) * _integralStride + x + 1;
                _visibleIntegral[i] = _visibleIntegral[i - _integralStride] + validCount;
                _brownIntegral[i] = _brownIntegral[i - _integralStride] + brownCount;
            }
        }
    }

    private int ExitColorSum(int[] integral, bool horizontal, int along, int first, int last)
    {
        var x0 = horizontal ? along : first;
        var x1 = horizontal ? along + 1 : last;
        var y0 = horizontal ? first : along;
        var y1 = horizontal ? last : along + 1;
        return integral[y1 * _integralStride + x1] - integral[y1 * _integralStride + x0]
            - integral[y0 * _integralStride + x1] + integral[y0 * _integralStride + x0];
    }
}
