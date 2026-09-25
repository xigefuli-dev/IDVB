using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace IDVBuff.Features.GameLaunch;

public sealed record FeverLoginResult(bool Success, string? Ticket, string? Message);

/// <summary>
/// Interacts directly with FeverGames' native mpay.dll to display the official NetEase
/// login / QR scan window and refresh the platform ticket without launching the full client.
/// Uses an isolated worker process to ensure DLL and file handles are 100% released on exit.
/// </summary>
public static class FeverLoginService
{
    private const string TicketMarker = "__FEVER_TICKET__:";
    private static readonly SemaphoreSlim LoginLock = new(1, 1);

    public static async Task<FeverLoginResult> StartLoginAsync(
        nint parentHwnd = 0,
        CancellationToken cancellationToken = default)
    {
        if (!await LoginLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new FeverLoginResult(false, null, "登录窗口正在进行中，请在弹出的窗口中完成操作。");

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                return await RunWorkerInSubprocessAsync(exePath, parentHwnd, cancellationToken).ConfigureAwait(false);
            }

            // Fallback for test harnesses where executable path cannot be launched
            return await RunWorkerInThreadAsync(parentHwnd, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LoginLock.Release();
        }
    }

    private static async Task<FeverLoginResult> RunWorkerInSubprocessAsync(
        string exePath,
        nint parentHwnd,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--fever-login-worker {parentHwnd} --isolated-dev-instance",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process is null)
            return new FeverLoginResult(false, null, "无法创建登录工作进程。");

        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) outputBuilder.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) errorBuilder.AppendLine(e.Data);
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            var output = outputBuilder.ToString();
            var markerIndex = output.IndexOf(TicketMarker, StringComparison.Ordinal);
            string? ticket = null;
            if (markerIndex >= 0)
            {
                var payload = output[(markerIndex + TicketMarker.Length)..].Trim();
                var firstLine = payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrEmpty(firstLine))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(firstLine);
                        ticket = Encoding.UTF8.GetString(bytes);
                    }
                    catch
                    {
                        ticket = firstLine;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(ticket))
            {
                return new FeverLoginResult(true, ticket.Trim(), "发烧登录成功，凭据已刷新。");
            }

            var err = errorBuilder.ToString().Trim();
            if (string.IsNullOrEmpty(err)) err = "未获取到有效登录凭据。";
            return new FeverLoginResult(false, null, err);
        }

        var error = errorBuilder.ToString().Trim();
        if (string.IsNullOrEmpty(error)) error = "用户取消了发烧登录。";
        return new FeverLoginResult(false, null, error);
    }

    /// <summary>
    /// Entry point for the isolated worker subprocess (--fever-login-worker).
    /// </summary>
    public static int RunWorker(nint parentHwnd)
    {
        if (!FeverEnvironmentResolver.TryResolve(out var mpayDllPath, out var skinZipPath, out var failureReason))
        {
            Console.Error.WriteLine(failureReason ?? "未找到发烧游戏组件");
            return 2;
        }

        var tcs = new TaskCompletionSource<FeverLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunMpayLoginThread(mpayDllPath, skinZipPath, parentHwnd, tcs, CancellationToken.None);
        var result = tcs.Task.GetAwaiter().GetResult();

        if (result.Success && !string.IsNullOrWhiteSpace(result.Ticket))
        {
            var ticketBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(result.Ticket.Trim()));
            Console.WriteLine($"{TicketMarker}{ticketBase64}");
            return 0;
        }

        Console.Error.WriteLine(result.Message ?? "登录未成功或未获取到凭据");
        return 1;
    }

    private static Task<FeverLoginResult> RunWorkerInThreadAsync(nint parentHwnd, CancellationToken ct)
    {
        if (!FeverEnvironmentResolver.TryResolve(out var mpayDllPath, out var skinZipPath, out var failureReason))
        {
            return Task.FromResult(new FeverLoginResult(false, null, failureReason ?? "未找到发烧游戏组件"));
        }

        var tcs = new TaskCompletionSource<FeverLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RunMpayLoginThread(mpayDllPath, skinZipPath, parentHwnd, tcs, ct);
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new FeverLoginResult(false, null, $"启动登录组件失败：{ex.Message}"));
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Name = "FeverMpayLoginThread";
        thread.Start();

        return tcs.Task;
    }

    private static void RunMpayLoginThread(
        string mpayDllPath,
        string skinZipPath,
        nint parentHwnd,
        TaskCompletionSource<FeverLoginResult> tcs,
        CancellationToken ct)
    {
        var dllDir = Path.GetDirectoryName(mpayDllPath)!;
        SetDllDirectory(dllDir);

        var hModule = LoadLibrary(mpayDllPath);
        if (hModule == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            tcs.TrySetResult(new FeverLoginResult(false, null, $"无法加载组件 mpay.dll (Win32Error: {err})"));
            return;
        }

        IntPtr instance = IntPtr.Zero;
        IntPtr cbObjPtr = IntPtr.Zero;
        IntPtr vtablePtr = IntPtr.Zero;
        ReleaseInterfaceDelegate? releaseFn = null;

        try
        {
            var pCreate = GetProcAddress(hModule, "?Create_Interface@CMpay_Interface@Mpay@@SAPEAV12@XZ");
            var pRelease = GetProcAddress(hModule, "?Release_Interface@CMpay_Interface@Mpay@@SAXXZ");
            var pSetRes = GetProcAddress(hModule, "?SetResPath@CMpay_Interface@Mpay@@QEAAHHPEBD@Z");
            var pSetOption = GetProcAddress(hModule, "?SetOption@CMpay_Interface@Mpay@@SAXPEBDPEBX@Z");
            var pInit = GetProcAddress(hModule, "?init@CMpay_Interface@Mpay@@QEAAHPEBD0000PEAVApiCallBack@2@H@Z");
            var pLogin = GetProcAddress(hModule, "?login@CMpay_Interface@Mpay@@QEAAXHH@Z");
            var pGetTicket = GetProcAddress(hModule, "?GetUserTicket@CMpay_Interface@Mpay@@QEAAXXZ");

            if (pRelease != IntPtr.Zero)
                releaseFn = Marshal.GetDelegateForFunctionPointer<ReleaseInterfaceDelegate>(pRelease);

            if (pCreate == IntPtr.Zero || pSetRes == IntPtr.Zero || pInit == IntPtr.Zero || pLogin == IntPtr.Zero)
            {
                tcs.TrySetResult(new FeverLoginResult(false, null, "mpay.dll 缺少必要的导出函数。"));
                return;
            }

            var createFn = Marshal.GetDelegateForFunctionPointer<CreateInterfaceDelegate>(pCreate);
            instance = createFn();
            if (instance == IntPtr.Zero)
            {
                tcs.TrySetResult(new FeverLoginResult(false, null, "创建 MPay 认证接口失败。"));
                return;
            }

            // Set skin path (type 2 = MPAY_RESOURCE_ZIP, encoded as ANSI/GBK)
            var setResFn = Marshal.GetDelegateForFunctionPointer<SetResPathDelegate>(pSetRes);
            var skinPathPtr = Marshal.StringToHGlobalAnsi(skinZipPath);
            try
            {
                setResFn(instance, 2, skinPathPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(skinPathPtr);
            }

            // Set parent window handle if available
            if (parentHwnd != 0 && pSetOption != IntPtr.Zero)
            {
                var setOptionFn = Marshal.GetDelegateForFunctionPointer<SetOptionDelegate>(pSetOption);
                var optionNamePtr = Marshal.StringToHGlobalAnsi("mpay_option_parent_hwnd");
                var hwndValPtr = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(hwndValPtr, parentHwnd);
                    setOptionFn(optionNamePtr, hwndValPtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(optionNamePtr);
                    Marshal.FreeHGlobal(hwndValPtr);
                }
            }

            // Build vtable and callback object
            var delegates = new List<Delegate>();
            string? capturedTicket = null;
            bool loginSuccess = false;

            void OnLoginFinish(IntPtr thisPtr, uint code, IntPtr extra)
            {
                Console.Error.WriteLine($"[FeverLogin] OnLoginFinish: code={code}, extra=0x{extra.ToInt64():X}");
                Debug.WriteLine($"[FeverLoginService] OnLoginFinish: code={code}, extra=0x{extra.ToInt64():X}");

                string? directTicket = null;
                if (extra != IntPtr.Zero)
                {
                    try
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            var ptr = Marshal.ReadIntPtr(extra, i * IntPtr.Size);
                            if (ptr != IntPtr.Zero)
                            {
                                var str = Marshal.PtrToStringUTF8(ptr);
                                Console.Error.WriteLine($"[FeverLogin] extra[{i}] = {str}");
                                if (i == 2 && !string.IsNullOrWhiteSpace(str))
                                {
                                    directTicket = str.Trim();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[FeverLogin] Error reading extra: {ex.Message}");
                    }
                }

                if (code == 0 || !string.IsNullOrWhiteSpace(directTicket))
                {
                    loginSuccess = true;
                    if (!string.IsNullOrWhiteSpace(directTicket))
                    {
                        capturedTicket = directTicket;
                        tcs.TrySetResult(new FeverLoginResult(true, capturedTicket, "发烧登录成功，凭据已刷新。"));
                        PostQuitMessage(0);
                        return;
                    }

                    if (pGetTicket != IntPtr.Zero)
                    {
                        var getTicketFn = Marshal.GetDelegateForFunctionPointer<GetUserTicketDelegate>(pGetTicket);
                        getTicketFn(instance);
                    }
                    else
                    {
                        PostQuitMessage(0);
                    }
                }
                else
                {
                    tcs.TrySetResult(new FeverLoginResult(false, null, $"登录完成回调返回非零状态码: code={code}"));
                    PostQuitMessage(0);
                }
            }

            void OnTicketResult(IntPtr thisPtr, int code, IntPtr ticketPtr, IntPtr extraPtr)
            {
                if (ticketPtr != IntPtr.Zero)
                {
                    capturedTicket = Marshal.PtrToStringUTF8(ticketPtr);
                }
                Console.Error.WriteLine($"[FeverLogin] OnTicketResult: code={code}, ticketLen={capturedTicket?.Length ?? 0}");
                Debug.WriteLine($"[FeverLoginService] OnTicketResult: code={code}, ticketLen={capturedTicket?.Length ?? 0}");
                if (code == 0 && !string.IsNullOrWhiteSpace(capturedTicket))
                {
                    tcs.TrySetResult(new FeverLoginResult(true, capturedTicket, "发烧登录成功，凭据已刷新。"));
                }
                else
                {
                    tcs.TrySetResult(new FeverLoginResult(false, null, $"获取 Ticket 失败: code={code}"));
                }
                PostQuitMessage(0);
            }

            NoArgsCallback dummyCb = _ => { };
            LoginFinishCallback finishCb = OnLoginFinish;
            TicketResultCallback ticketCb = OnTicketResult;

            delegates.Add(dummyCb);      // 0
            delegates.Add(finishCb);     // 1: on_login_finish
            delegates.Add(dummyCb);      // 2: on_logout
            delegates.Add(dummyCb);      // 3
            delegates.Add(dummyCb);      // 4
            delegates.Add(ticketCb);     // 5: on_ticket_result
            delegates.Add(ticketCb);     // 6

            for (int i = delegates.Count; i < 32; i++)
                delegates.Add(dummyCb);

            vtablePtr = Marshal.AllocHGlobal(IntPtr.Size * delegates.Count);
            for (int i = 0; i < delegates.Count; i++)
            {
                var fPtr = Marshal.GetFunctionPointerForDelegate(delegates[i]);
                Marshal.WriteIntPtr(vtablePtr, i * IntPtr.Size, fPtr);
            }

            cbObjPtr = Marshal.AllocHGlobal(IntPtr.Size + 4096);
            Marshal.WriteIntPtr(cbObjPtr, 0, vtablePtr);

            // Initialize mpay for Identity V (game_id: "h55", channel: "a50_sdk_cn")
            var initFn = Marshal.GetDelegateForFunctionPointer<InitDelegate>(pInit);
            var pGame = Marshal.StringToHGlobalAnsi("h55");
            var pChannel = Marshal.StringToHGlobalAnsi("a50_sdk_cn");
            var pEmpty = Marshal.StringToHGlobalAnsi("");

            int initResult;
            try
            {
                initResult = initFn(instance, pGame, pEmpty, pChannel, pEmpty, pEmpty, cbObjPtr, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(pGame);
                Marshal.FreeHGlobal(pChannel);
                Marshal.FreeHGlobal(pEmpty);
            }

            Debug.WriteLine($"[FeverLoginService] initResult={initResult}");

            // Call login(instance, 1, 0)
            var loginFn = Marshal.GetDelegateForFunctionPointer<LoginDelegate>(pLogin);
            loginFn(instance, 1, 0);

            // Run Windows message pump
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetResult(new FeverLoginResult(false, null, "用户取消了发烧登录。"));
                    break;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (loginSuccess && !tcs.Task.IsCompleted)
            {
                tcs.TrySetResult(new FeverLoginResult(true, capturedTicket, "发烧登录成功，凭据已刷新。"));
            }
        }
        finally
        {
            try { releaseFn?.Invoke(); } catch { }
            if (cbObjPtr != IntPtr.Zero) Marshal.FreeHGlobal(cbObjPtr);
            if (vtablePtr != IntPtr.Zero) Marshal.FreeHGlobal(vtablePtr);
            FreeLibrary(hModule);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInterfaceDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseInterfaceDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetResPathDelegate(IntPtr instance, int type, IntPtr path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetOptionDelegate(IntPtr name, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(
        IntPtr instance, IntPtr gameId, IntPtr reserved1, IntPtr appChannel,
        IntPtr reserved2, IntPtr reserved3, IntPtr callback, int flag);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoginDelegate(IntPtr instance, int mode, int style);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetUserTicketDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NoArgsCallback(IntPtr thisPtr);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoginFinishCallback(IntPtr thisPtr, uint code, IntPtr extra);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TicketResultCallback(IntPtr thisPtr, int code, IntPtr ticket, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpLibFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);
}
