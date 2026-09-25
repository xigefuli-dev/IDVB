using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace IDVBuff.Features.GameLaunch;

/// <summary>
/// Provides an embedded NetEase Fever-compatible IPC bridge host.
/// It registers the "LHMW_FG_Main" message-only window and responds to Identity V's
/// WM_COPYDATA authentication ticket requests, allowing the game to launch cleanly
/// without the official NetEase Fever launcher running in the background.
/// </summary>
internal sealed class FeverIpcBridge : IDisposable
{
    private static readonly Lazy<FeverIpcBridge> LazyInstance = new(() => new FeverIpcBridge());
    public static FeverIpcBridge Instance => LazyInstance.Value;

    private const string WindowClassName = "LHMW_FG_Main";
    private const uint WmCopyData = 0x004A;
    private const uint SmtoBlock = 0x0002;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly object _stateLock = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private string? _currentTicket;
    private readonly ManualResetEventSlim _startedEvent = new(false);
    private bool _isDisposed;

    // 496-byte static XOR key table reversed from Netease.Ncg.Pay.dll (RVA 0x234060)
    private static readonly byte[] IpcKeyTable =
    [
        0x4C, 0x85, 0x66, 0x7C, 0xF2, 0x24, 0x97, 0x66, 0x23, 0x00, 0x81, 0x9A, 0x8E, 0x03, 0xCB, 0xC7,
        0x90, 0x1D, 0x15, 0xEA, 0x46, 0x3D, 0x7B, 0xAC, 0xC3, 0x6F, 0xBD, 0xA7, 0x27, 0x78, 0x13, 0xB4,
        0x8E, 0x7C, 0xEE, 0x31, 0x1A, 0xA2, 0xA7, 0x2D, 0x09, 0xE5, 0xB3, 0x15, 0xDE, 0x16, 0x37, 0x9C,
        0x94, 0x0C, 0x7F, 0x53, 0x94, 0x90, 0x07, 0xA7, 0xB0, 0x16, 0x53, 0x4C, 0x32, 0xF6, 0x41, 0xDC,
        0x88, 0xE4, 0x17, 0x5E, 0xB1, 0x03, 0x8C, 0xD4, 0x84, 0x56, 0xEE, 0x4E, 0x10, 0x8C, 0x20, 0xF3,
        0x64, 0x8C, 0xC5, 0x91, 0xAE, 0x75, 0xF3, 0x43, 0xA3, 0xD6, 0x43, 0x16, 0xCF, 0x93, 0x29, 0xBD,
        0xCB, 0x85, 0x81, 0xC8, 0xDB, 0x10, 0x0D, 0x1A, 0x05, 0xF0, 0x87, 0xCB, 0x13, 0x18, 0xF5, 0xC4,
        0xF9, 0x94, 0xEE, 0xB5, 0x73, 0xD0, 0x5C, 0x1E, 0x41, 0xC9, 0x6F, 0x40, 0x9B, 0x56, 0x18, 0x46,
        0x3E, 0xE0, 0xD3, 0xF8, 0xA2, 0x69, 0x01, 0x17, 0x0C, 0xA5, 0xC6, 0xFD, 0x2A, 0x1E, 0xC9, 0x0C,
        0xFE, 0xA2, 0x1C, 0x3D, 0xE8, 0xEC, 0xF9, 0xD4, 0x06, 0x22, 0x0A, 0x3F, 0xF7, 0xB6, 0xDA, 0x73,
        0xC8, 0xC4, 0xEF, 0xEF, 0xFA, 0x33, 0x06, 0x24, 0x99, 0xAD, 0xF6, 0x1B, 0xA3, 0x2D, 0x23, 0x0F,
        0x45, 0xD4, 0xD1, 0x6C, 0x59, 0x47, 0x64, 0x77, 0xA6, 0x53, 0xE4, 0xA5, 0x2A, 0xAF, 0x62, 0xDC,
        0xC1, 0x89, 0x7B, 0xFB, 0x9B, 0xFD, 0x03, 0xEF, 0xFF, 0x93, 0x2C, 0x1C, 0xD5, 0x9C, 0x6E, 0x91,
        0x96, 0x8B, 0x19, 0xBD, 0xC8, 0x25, 0x31, 0x28, 0x8A, 0x1A, 0xCB, 0x3A, 0x92, 0xDF, 0xFA, 0x09,
        0x7B, 0x0D, 0x91, 0x54, 0xAD, 0xDC, 0x33, 0x79, 0x85, 0x1A, 0x41, 0xA2, 0x57, 0x46, 0xAE, 0x6D,
        0x23, 0xBB, 0x24, 0x80, 0xB1, 0x55, 0xB6, 0x8B, 0x29, 0x7F, 0xD9, 0xD2, 0x2C, 0x2E, 0x5F, 0xBC,
        0xCF, 0xD1, 0xBE, 0xD0, 0x15, 0x75, 0xF6, 0x3A, 0x0B, 0x96, 0x6D, 0xA3, 0xB1, 0x92, 0x71, 0xA2,
        0x2C, 0x22, 0x4C, 0x9D, 0xDE, 0x51, 0xB0, 0x24, 0x7B, 0x3A, 0x2B, 0xCA, 0xC4, 0x47, 0x63, 0xB4,
        0x47, 0xC1, 0x3C, 0x0C, 0xEC, 0xE7, 0x80, 0x00, 0xA0, 0xD1, 0x91, 0xF9, 0x37, 0x9A, 0xF2, 0xBE,
        0x05, 0x2F, 0x21, 0xA9, 0x01, 0x5A, 0x72, 0x20, 0xFB, 0x4C, 0x91, 0x44, 0x99, 0xC9, 0xC6, 0x76,
        0xF4, 0xFE, 0xBA, 0x3B, 0xF9, 0x19, 0x12, 0x92, 0xB9, 0x2A, 0x82, 0xD7, 0xC4, 0xD0, 0xEC, 0x25,
        0x62, 0xAB, 0x17, 0x61, 0xB5, 0x95, 0x80, 0x7B, 0x70, 0x35, 0x9A, 0xFB, 0x88, 0x9A, 0xC1, 0xA3,
        0xD3, 0x14, 0xC0, 0x02, 0x57, 0x68, 0x45, 0x14, 0xD4, 0x4C, 0x24, 0x29, 0x94, 0xAE, 0xC3, 0xA5,
        0xEF, 0x4C, 0xC1, 0x4E, 0x41, 0x73, 0xC2, 0xE3, 0x24, 0xF6, 0x24, 0xE2, 0xFD, 0x4D, 0xEC, 0x15,
        0x48, 0xE2, 0x5D, 0xB7, 0xC3, 0x36, 0xAD, 0x80, 0x7B, 0x44, 0x6F, 0x9A, 0xB8, 0xAB, 0x32, 0xBE,
        0x23, 0x80, 0x6C, 0x71, 0xA3, 0xBC, 0xBF, 0x77, 0xF0, 0xF1, 0x31, 0xB5, 0x72, 0x02, 0x1E, 0x2A,
        0xC9, 0x46, 0x0E, 0x9F, 0x25, 0xAA, 0x14, 0x2D, 0xC5, 0x2B, 0x2D, 0xBA, 0x54, 0xC1, 0x0E, 0x85,
        0x13, 0xC7, 0x9B, 0x87, 0xA0, 0x20, 0xA3, 0x01, 0x39, 0x45, 0xA3, 0x3A, 0xEF, 0xB3, 0x89, 0x4C,
        0x54, 0xCF, 0x1B, 0x98, 0x05, 0x5F, 0x37, 0x3E, 0x87, 0x7B, 0x10, 0xA6, 0x83, 0x0D, 0x32, 0xBF,
        0x40, 0x38, 0x42, 0x0E, 0x86, 0x27, 0x67, 0x7A, 0x4B, 0x5B, 0x57, 0xA5, 0xA7, 0x3C, 0x03, 0x9F,
        0x76, 0xE5, 0x05, 0xC6, 0x56, 0x98, 0x9E, 0xFF, 0xF5, 0x28, 0x4B, 0x90, 0x11, 0x25, 0xCB, 0x78,
    ];

    public void Start(string? initialTicket = null)
    {
        lock (_stateLock)
        {
            if (!string.IsNullOrEmpty(initialTicket))
            {
                _currentTicket = initialTicket;
            }

            if (_thread is not null && _hwnd != IntPtr.Zero)
            {
                return;
            }

            _startedEvent.Reset();
            _thread = new Thread(RunMessageLoop)
            {
                Name = "FeverIpcBridge",
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        _startedEvent.Wait(TimeSpan.FromSeconds(3));
    }

    public void SetTicket(string? ticket)
    {
        lock (_stateLock)
        {
            _currentTicket = ticket;
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_hwnd != IntPtr.Zero)
            {
                PostMessageW(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                _hwnd = IntPtr.Zero;
            }

            _thread = null;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Stop();
        _startedEvent.Dispose();
    }

    private void RunMessageLoop()
    {
        IntPtr hInstance = GetModuleHandleW(null);
        var wndProcDelegate = new WndProcDelegate(WndProc);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };

        ushort atom = RegisterClassExW(ref wc);
        if (atom == 0)
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_CLASS_ALREADY_EXISTS = 1410
            if (err != 1410)
            {
                Debug.WriteLine($"[FeverIpcBridge] RegisterClassExW failed with code {err}");
            }
        }

        _hwnd = CreateWindowExW(
            0,
            WindowClassName,
            WindowClassName,
            0,
            0, 0, 0, 0,
            HwndMessage,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Debug.WriteLine($"[FeverIpcBridge] CreateWindowExW failed with code {Marshal.GetLastWin32Error()}");
            _startedEvent.Set();
            return;
        }

        // Allow WM_COPYDATA through Windows UIPI filter (in case game runs elevated)
        try
        {
            ChangeWindowMessageFilterEx(_hwnd, WmCopyData, MsgfltAllow, IntPtr.Zero);
            ChangeWindowMessageFilter(WmCopyData, 1 /* MSGFLT_ADD */);
        }
        catch { }

        Debug.WriteLine($"[FeverIpcBridge] LHMW_FG_Main created successfully: hwnd=0x{_hwnd.ToInt64():X}");
        _startedEvent.Set();

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        UnregisterClassW(WindowClassName, hInstance);
        // Keep delegate alive
        GC.KeepAlive(wndProcDelegate);
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
    {
        if (uMsg == WmCopyData)
        {
            try
            {
                var handled = HandleCopyData(wParam, lParam);
                if (handled) return new IntPtr(1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FeverIpcBridge] Exception handling WM_COPYDATA: {ex}");
            }
        }

        return DefWindowProcW(hWnd, uMsg, wParam, lParam);
    }

    private bool HandleCopyData(IntPtr senderHwnd, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return false;

        var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
        var dwData = cds.dwData.ToUInt32();

        int opcode = 0;
        if (cds.cbData >= 12 && cds.lpData != IntPtr.Zero)
        {
            var header = new byte[12];
            Marshal.Copy(cds.lpData, header, 0, 12);
            opcode = BitConverter.ToInt32(header, 4);
        }

        Debug.WriteLine($"[FeverIpcBridge] Received WM_COPYDATA: dwData={dwData}, opcode={opcode}, cbData={cds.cbData}, sender=0x{senderHwnd.ToInt64():X}");

        // dwData 13 or opcode 13: Identity V client requesting ticket
        if (dwData == 13 || opcode == 13)
        {
            string? ticket = null;
            lock (_stateLock)
            {
                ticket = _currentTicket;
            }

            if (string.IsNullOrEmpty(ticket))
            {
                ticket = FeverAccountStore.Instance.GetActiveAccountTicket();
            }

            if (string.IsNullOrEmpty(ticket))
            {
                Debug.WriteLine("[FeverIpcBridge] Warning: No active ticket found to respond to client!");
                return false;
            }

            SendTicket(senderHwnd, ticket);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Creates the standard Fever-compatible authentication packet.
    /// Format:
    /// - [0..3]: Int32 payload length (len(payload))
    /// - [4..7]: Int32 command code (14 / 0x0E)
    /// - [8..11]: Int32 flags/reserved (0)
    /// - [12..]: UTF-8 JSON payload `{"login_channel":"netease","ticket":"..."}`, XOR encrypted with IpcKeyTable.
    /// </summary>
    internal static byte[] CreateTicketPacket(string ticket, string loginChannel = "netease")
    {
        var payloadObj = new { login_channel = loginChannel, ticket = ticket.Trim() };
        var payloadJson = JsonSerializer.Serialize(payloadObj);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        var packet = new byte[12 + payloadBytes.Length];

        // 1. Header: <iii (length, opcode=14, flags=0)
        BitConverter.GetBytes(payloadBytes.Length).CopyTo(packet, 0);
        BitConverter.GetBytes(14).CopyTo(packet, 4);
        BitConverter.GetBytes(0).CopyTo(packet, 8);

        // 2. Encrypt payload with 496-byte IpcKeyTable
        for (int i = 0; i < payloadBytes.Length; i++)
        {
            packet[12 + i] = (byte)(payloadBytes[i] ^ IpcKeyTable[i % IpcKeyTable.Length]);
        }

        return packet;
    }

    /// <summary>
    /// Decrypts the standard authentication packet for verification and testing.
    /// </summary>
    internal static string DecryptTicketPacket(byte[] packet)
    {
        if (packet.Length < 12)
            throw new ArgumentException("Packet too short", nameof(packet));

        int payloadLen = BitConverter.ToInt32(packet, 0);
        int cmd = BitConverter.ToInt32(packet, 4);

        if (cmd != 14)
            throw new InvalidOperationException($"Unexpected command: {cmd}");

        if (packet.Length < 12 + payloadLen)
            throw new ArgumentException("Packet truncated", nameof(packet));

        var decrypted = new byte[payloadLen];
        for (int i = 0; i < payloadLen; i++)
        {
            decrypted[i] = (byte)(packet[12 + i] ^ IpcKeyTable[i % IpcKeyTable.Length]);
        }

        var json = Encoding.UTF8.GetString(decrypted);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("ticket", out var ticketProp))
            return ticketProp.GetString() ?? string.Empty;

        return json;
    }

    private void SendTicket(IntPtr targetHwnd, string ticket)
    {
        var packet = CreateTicketPacket(ticket);

        // Send WM_COPYDATA with dwData=14 to target client window
        var handle = GCHandle.Alloc(packet, GCHandleType.Pinned);
        try
        {
            var responseCds = new COPYDATASTRUCT
            {
                dwData = new UIntPtr(14),
                cbData = packet.Length,
                lpData = handle.AddrOfPinnedObject()
            };

            Debug.WriteLine($"[FeverIpcBridge] Sending native ticket packet to 0x{targetHwnd.ToInt64():X}, packetLen={packet.Length}");
            _ = SendMessageTimeoutW(
                targetHwnd,
                WmCopyData,
                _hwnd,
                ref responseCds,
                SmtoBlock,
                5000,
                out var result);

            Debug.WriteLine($"[FeverIpcBridge] Ticket sent, result={result.ToInt64()}");
        }
        finally
        {
            handle.Free();
        }
    }

    #region Win32 P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public UIntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr pChangeFilterStruct);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilter(uint message, uint dwFlag);

    private const uint MsgfltAllow = 1;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        ref COPYDATASTRUCT lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    #endregion
}
