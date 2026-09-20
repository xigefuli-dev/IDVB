using System.Runtime.InteropServices;

namespace IDVBuff.Plugins.NightVision;

/// <summary>
/// Applies a full-screen linear RGB gain through the Magnification API. The
/// API only accepts a 5x5 color matrix, so this filter cannot selectively
/// enhance low-luminance pixels. This deliberately does not create a topmost
/// overlay window: the desktop compositor applies the effect directly, so the
/// active application continues to receive mouse and keyboard input normally.
/// </summary>
internal sealed class NightVisionFilter : IDisposable
{
    private readonly object _gate = new();
    private double _brightnessPercent = 100;
    private MAGCOLOREFFECT? _previousEffect;
    private bool _enabled;
    private bool _initialized;

    public void SetBrightnessPercent(double percent)
    {
        lock (_gate)
        {
            _brightnessPercent = Math.Clamp(percent, 0, NightVisionPlugin.MaximumBrightnessPercent);
            if (_enabled)
                _ = TryApplyEffect();
        }
    }

    public void Toggle()
    {
        lock (_gate)
        {
            if (_enabled)
                DisableCore();
            else
                EnableCore();
        }
    }

    public void Disable()
    {
        lock (_gate)
            DisableCore();
    }

    private void EnableCore()
    {
        if (!EnsureInitialized() || !CapturePreviousEffect() || !TryApplyEffect())
            return;

        _enabled = true;
    }

    private void DisableCore()
    {
        if (!_initialized)
        {
            _enabled = false;
            return;
        }

        var effect = _previousEffect ?? CreateIdentityEffect();
        _ = MagSetFullscreenColorEffect(ref effect);
        _enabled = false;
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
            return true;

        if (!MagInitialize())
            return false;

        _initialized = true;
        return true;
    }

    private bool CapturePreviousEffect()
    {
        var previousEffect = CreateIdentityEffect();
        _previousEffect = MagGetFullscreenColorEffect(ref previousEffect)
            ? previousEffect
            : null;
        return true;
    }

    private bool TryApplyEffect()
    {
        var effect = CreateNightVisionEffect(_brightnessPercent);
        return MagSetFullscreenColorEffect(ref effect);
    }

    private static MAGCOLOREFFECT CreateNightVisionEffect(double brightnessPercent)
    {
        // Magnification's fullscreen effect is limited to an affine color
        // matrix. This is therefore a global linear gain: it affects every
        // RGB channel equally and can clip highlights at higher values.
        var lift = (float)(brightnessPercent / 100d);
        var gain = 1f + Math.Min(lift, 20f);
        return new MAGCOLOREFFECT
        {
            Transform =
            [
                gain, 0, 0, 0, 0,
                0, gain, 0, 0, 0,
                0, 0, gain, 0, 0,
                0, 0, 0, 1, 0,
                0, 0, 0, 0, 1
            ]
        };
    }

    private static MAGCOLOREFFECT CreateIdentityEffect() => new()
    {
        Transform =
        [
            1, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 1, 0, 0,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1
        ]
    };

    public void Dispose()
    {
        lock (_gate)
        {
            DisableCore();
            if (_initialized)
                MagUninitialize();
            _previousEffect = null;
            _initialized = false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] Transform;
    }

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagGetFullscreenColorEffect(ref MAGCOLOREFFECT effect);

    [DllImport("Magnification.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT effect);
}
