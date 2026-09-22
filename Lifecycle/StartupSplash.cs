using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using IDVBuff.Diagnostics;
using Forms = System.Windows.Forms;

namespace IDVBuff.Lifecycle;

/// <summary>A small independent message loop keeps startup feedback alive while WinUI loads.</summary>
internal static class StartupSplash
{
    private static IntPtr _targetWindow;

    public static void SetTargetWindow(IntPtr windowHandle) => Volatile.Write(ref _targetWindow, windowHandle);

    [Flags]
    internal enum Stage { Interface = 1, Window = 2, Services = 4, Maps = 8, Extensions = 16, Catalog = 32, Ready = 64 }
    private static int _planned = 127;
    private static int _completed;
    private static int _transition;
    private static long _fadeStarted;
    private static readonly TaskCompletionSource TransitionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static readonly TaskCompletionSource Dismissed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Configure(bool safeMode) => _planned = safeMode
        ? (int)(Stage.Interface | Stage.Window | Stage.Catalog | Stage.Ready) : 127;

    public static void Complete(Stage stage)
    {
        var before = Interlocked.Or(ref _completed, (int)stage);
        if ((before & (int)stage) == 0)
            StartupTimeline.Write($"Startup step completed: {stage}.");
    }

    public static async Task PrepareTransitionAsync()
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _closed) != 0) return;
        Interlocked.Exchange(ref _transition, 1);
        await Task.WhenAny(TransitionReady.Task, Task.Delay(500));
    }

    public static async Task FadeOutAsync()
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _closed) != 0) return;
        Interlocked.Exchange(ref _fadeStarted, Stopwatch.GetTimestamp());
        await Task.WhenAny(Dismissed.Task, Task.Delay(600));
        Close();
    }
    private static string _status = "正在启动…";
    private static int _closed;
    private static int _started;
    private static readonly ManualResetEventSlim FirstPaint = new(false);

    public static void Show()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        var thread = new Thread(() =>
        {
            try
            {
                using var splash = new SplashForm();
                if (Volatile.Read(ref _closed) == 0)
                    Forms.Application.Run(splash);
            }
            catch (Exception exception)
            {
                StartupTimeline.Write("Native startup splash unavailable; application startup continues.", exception);
            }
            finally { FirstPaint.Set(); TransitionReady.TrySetResult(); Dismissed.TrySetResult(); }
        }) { IsBackground = true, Name = "IDVB startup splash" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // A bounded wait gives the splash a chance to paint before WinUI's loader work.
        // Failure to create a decorative window must never prevent application startup.
        if (!FirstPaint.Wait(TimeSpan.FromSeconds(2)))
            StartupTimeline.Write("Native splash first Paint wait timed out; startup continues.");
    }

    public static void Report(string status) => Volatile.Write(ref _status, status);
    public static void Close() => Interlocked.Exchange(ref _closed, 1);

    public static async Task CloseAsync()
    {
        Close();
        if (Volatile.Read(ref _started) != 0)
        {
            await Task.WhenAny(Dismissed.Task, Task.Delay(200));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly IntPtr HwndTopMost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    private sealed class SplashForm : Forms.Form
    {
        private readonly Forms.Timer _timer = new() { Interval = 16 };
        private readonly Font _titleFont = new("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font _statusFont = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        private Bitmap? _logo;
        private bool _paintRecorded;
        private long _lastTopmostRefresh;
        private bool _topmostFailureRecorded;

        public SplashForm()
        {
            Text = "Identity Vision Bridge";
            FormBorderStyle = Forms.FormBorderStyle.None;
            StartPosition = Forms.FormStartPosition.Manual;
            ShowInTaskbar = false;
            MaximizeBox = MinimizeBox = false;
            // The splash has its own UI thread and must remain visible while WinUI is loading.
            // Waiting until the handoff leaves it behind whichever window activated meanwhile.
            TopMost = true;
            // One scale for layout, fonts and logo; avoid automatic DPI plus manual scaling twice.
            AutoScaleMode = Forms.AutoScaleMode.None;
            ClientSize = new Size(460, 260);
            BackColor = Color.FromArgb(250, 251, 253);
            DoubleBuffered = true;
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "IDVB_icon_multisize.ico");
                using var icon = new Icon(path, new Size(256, 256));
                _logo = icon.ToBitmap();
            }
            catch (Exception exception)
            {
                StartupTimeline.Write("Startup splash icon unavailable.", exception);
            }
            _timer.Tick += (_, _) =>
            {
                if (Volatile.Read(ref _closed) != 0) { Close(); return; }
                // Other topmost windows (including overlays created during WinUI startup)
                // can enter above this form after its one-time TopMost assignment.
                // Keep the splash at the front of that band without taking keyboard focus.
                if (Stopwatch.GetElapsedTime(_lastTopmostRefresh).TotalMilliseconds >= 100)
                    RefreshTopmost();
                if (Volatile.Read(ref _transition) != 0)
                {
                    TransitionReady.TrySetResult();
                }
                var fade = Volatile.Read(ref _fadeStarted);
                if (fade != 0)
                {
                    var t = Math.Clamp((Stopwatch.GetElapsedTime(fade).TotalMilliseconds - 30) / 340.0, 0, 1);
                    Opacity = 1 - t * t * (3 - 2 * t);
                    if (t >= 1) { Close(); return; }
                }
                Invalidate();
            };
            _timer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshTopmost();
        }

        private void RefreshTopmost()
        {
            _lastTopmostRefresh = Stopwatch.GetTimestamp();
            if (SetWindowPos(Handle, HwndTopMost, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate))
                return;
            if (_topmostFailureRecorded)
                return;
            _topmostFailureRecorded = true;
            StartupTimeline.Write($"Startup splash topmost refresh failed: Win32 error {Marshal.GetLastWin32Error()}.");
        }

        internal static Rectangle CalculateBounds(Rectangle workArea, int dpi)
        {
            var dpiScale = Math.Max(1, dpi / 96d);
            var relativeScale = Math.Clamp(Math.Min(workArea.Width / dpiScale / 1920,
                workArea.Height / dpiScale / 1080), 0.85, 1.25);
            var scale = Math.Min(dpiScale * relativeScale,
                Math.Min(workArea.Width * 0.8 / 460, workArea.Height * 0.8 / 260));
            var width = Math.Max(1, (int)Math.Round(460 * scale));
            var height = Math.Max(1, (int)Math.Round(260 * scale));
            return new Rectangle(workArea.X + (workArea.Width - width) / 2,
                workArea.Y + (workArea.Height - height) / 2, width, height);
        }

        protected override void OnLoad(EventArgs e)
        {
            // Choose the monitor containing the pointer, including monitors with negative origins.
            var workArea = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
            Location = new Point(workArea.X + workArea.Width / 2, workArea.Y + workArea.Height / 2);
            Bounds = CalculateBounds(workArea, DeviceDpi);
            StartupTimeline.Write($"Startup splash layout: dpi={DeviceDpi}; workArea={workArea}; bounds={Bounds}.");
            base.OnLoad(e);
        }

        protected override void OnDpiChanged(Forms.DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            Bounds = CalculateBounds(Forms.Screen.FromRectangle(e.SuggestedRectangle).WorkingArea, e.DeviceDpiNew);
        }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.ScaleTransform(ClientSize.Width / 460f, ClientSize.Height / 260f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(Color.FromArgb(224, 229, 237));
            g.DrawRectangle(border, 0, 0, 459, 259);
            if (_logo is not null) g.DrawImage(_logo, 36, 34, 62, 62);
            using var title = new SolidBrush(Color.FromArgb(28, 38, 56));
            using var muted = new SolidBrush(Color.FromArgb(101, 112, 130));
            using var accent = new SolidBrush(Color.FromArgb(48, 112, 224));
            using var track = new SolidBrush(Color.FromArgb(228, 234, 243));
            g.DrawString("Identity Vision Bridge", _titleFont, title, 34, 117);
            g.DrawString(Volatile.Read(ref _status), _statusFont, muted, 35, 183);
            var total = BitOperations.PopCount((uint)Volatile.Read(ref _planned));
            var done = BitOperations.PopCount((uint)(Volatile.Read(ref _completed) & Volatile.Read(ref _planned)));
            using var right = new StringFormat { Alignment = StringAlignment.Far };
            g.DrawString($"{done}/{total} 项", _statusFont, muted, new RectangleF(350, 183, 74, 24), right);
            g.FillRectangle(track, 36, 220, 388, 3);
            g.FillRectangle(accent, 36, 220, 388f * done / total, 3);
            if (!_paintRecorded)
            {
                _paintRecorded = true;
                StartupTimeline.Write("Native startup splash first Paint (before DWM presentation).");
                FirstPaint.Set();
            }
        }

        protected override void OnFormClosing(Forms.FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            TopMost = false;
            var target = Volatile.Read(ref _targetWindow);
            if (target != IntPtr.Zero)
            {
                SetForegroundWindow(target);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _logo?.Dispose();
                _titleFont.Dispose();
                _statusFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
