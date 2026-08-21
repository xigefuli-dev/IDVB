using System.Diagnostics;
using System.Runtime.InteropServices;
using IDVBuff.Core.Contracts;

namespace IDVBuff.Features.Capture;

/// <summary>
/// Win32 implementation of the host capture-protection contract. It owns the
/// policy and every registered HWND so a newly-created window immediately
/// inherits the current live-mode settings.
/// </summary>
public sealed class WindowCaptureProtectionService : ICaptureProtectionService
{
    private const uint WdaNone = 0x0;
    private const uint WdaExcludeFromCapture = 0x11;
    private readonly object _gate = new();
    private readonly Dictionary<long, Registration> _registrations = [];
    private bool _disposed;
    private bool _pluginEnabled;
    private bool _hideMainProgram;
    private bool _hideDisplayLayer;

    public bool IsPluginEnabled
    {
        get { lock (_gate) return _pluginEnabled; }
    }

    public bool IsProtectionRequested(CaptureProtectionWindowCategory category)
    {
        lock (_gate)
            return IsProtectionRequestedCore(category);
    }

    public void SetPolicy(bool pluginEnabled, bool hideMainProgram, bool hideDisplayLayer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pluginEnabled = pluginEnabled;
            _hideMainProgram = hideMainProgram;
            _hideDisplayLayer = hideDisplayLayer;
            RefreshPolicyCore();
        }
    }

    public ICaptureProtectionRegistration RegisterWindow(
        IntPtr handle,
        CaptureProtectionWindowCategory category,
        string name)
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentException("窗口句柄不能为空。", nameof(handle));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("窗口名称不能为空。", nameof(name));

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registrations.TryGetValue(handle.ToInt64(), out var previous))
                previous.Dispose();
            var registration = new Registration(this, handle, category, name.Trim());
            _registrations[handle.ToInt64()] = registration;
            ApplyWindowCore(registration);
            return registration;
        }
    }

    public void RefreshPolicy()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RefreshPolicyCore();
        }
    }

    private bool IsProtectionRequestedCore(CaptureProtectionWindowCategory category) =>
        _pluginEnabled && category switch
        {
            CaptureProtectionWindowCategory.MainProgram => _hideMainProgram,
            CaptureProtectionWindowCategory.DisplayLayer => _hideDisplayLayer,
            _ => false
        };

    private void RefreshPolicyCore()
    {
        foreach (var registration in _registrations.Values.ToArray())
            ApplyWindowCore(registration);
    }

    private void ApplyWindowCore(Registration registration)
    {
        var requested = IsProtectionRequestedCore(registration.Category);
        var affinity = requested ? WdaExcludeFromCapture : WdaNone;
        if (TrySetAffinity(registration.Handle, affinity, out var error))
        {
            registration.SetApplied(requested);
            return;
        }

        // A failed enable must never make a window disappear or stop the app.
        registration.SetApplied(false);
        Log(
            $"捕获保护应用失败：name={registration.Name}, "
            + $"category={registration.Category}, requested={requested}, {error}");
    }

    private void Unregister(Registration registration)
    {
        lock (_gate)
        {
            if (!_registrations.Remove(registration.Handle.ToInt64()))
                return;
            // Best effort cleanup before the HWND can be destroyed/reused.
            if (!TrySetAffinity(registration.Handle, WdaNone, out var error))
                Log($"捕获保护注销失败：name={registration.Name}, {error}");
            registration.SetApplied(false);
        }
    }

    private static bool TrySetAffinity(IntPtr handle, uint affinity, out string error)
    {
        error = string.Empty;
        try
        {
            SetLastError(0);
            if (SetWindowDisplayAffinity(handle, affinity))
                return true;
            error = $"Win32={Marshal.GetLastWin32Error()}";
            return false;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException
                or EntryPointNotFoundException
                or SEHException
                or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[CaptureProtection] {message}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var registration in _registrations.Values.ToArray())
            {
                if (!TrySetAffinity(registration.Handle, WdaNone, out var error))
                    Log($"捕获保护释放失败：name={registration.Name}, {error}");
                registration.SetApplied(false);
            }
            _registrations.Clear();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("kernel32.dll")]
    private static extern void SetLastError(uint dwErrCode);

    private sealed class Registration : ICaptureProtectionRegistration
    {
        private readonly WindowCaptureProtectionService _owner;
        private bool _disposed;

        public Registration(
            WindowCaptureProtectionService owner,
            IntPtr handle,
            CaptureProtectionWindowCategory category,
            string name)
        {
            _owner = owner;
            Handle = handle;
            Category = category;
            Name = name;
        }

        public IntPtr Handle { get; }

        public CaptureProtectionWindowCategory Category { get; }

        public string Name { get; }

        public bool IsProtectionApplied { get; private set; }

        public void SetApplied(bool applied) => IsProtectionApplied = applied;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner.Unregister(this);
        }
    }
}
