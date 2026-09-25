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
public static partial class FeverLoginService
{
    private const string TicketMarker = "__FEVER_TICKET__:";
    private static readonly SemaphoreSlim LoginLock = new(1, 1);

    public static async Task<FeverLoginResult> StartLoginAsync(
        nint parentHwnd = 0,
        bool isLongTerm = false,
        CancellationToken cancellationToken = default)
    {
        if (!await LoginLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return new FeverLoginResult(false, null, "登录窗口正在进行中，请在弹出的窗口中完成操作。");

        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                return await RunWorkerInSubprocessAsync(exePath, parentHwnd, isLongTerm, cancellationToken).ConfigureAwait(false);
            }

            // Fallback for test harnesses where executable path cannot be launched
            return await RunWorkerInThreadAsync(parentHwnd, isLongTerm, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            LoginLock.Release();
        }
    }

    private static async Task<FeverLoginResult> RunWorkerInSubprocessAsync(
        string exePath,
        nint parentHwnd,
        bool isLongTerm,
        CancellationToken cancellationToken)
    {
        var longTermArg = isLongTerm ? " --long-term" : "";
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--fever-login-worker {parentHwnd}{longTermArg} --isolated-dev-instance",
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

            // In long-term login, MPay may successfully persist session database without returning ticket immediately
            if (isLongTerm)
            {
                return new FeverLoginResult(true, null, "发烧平台长期凭据已保存。");
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
    public static int RunWorker(nint parentHwnd, bool isLongTerm = false)
    {
        if (!FeverEnvironmentResolver.TryResolve(out var mpayDllPath, out var skinZipPath, out var failureReason))
        {
            Console.Error.WriteLine(failureReason ?? "未找到发烧游戏组件");
            return 2;
        }

        var tcs = new TaskCompletionSource<FeverLoginResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunMpayLoginThread(mpayDllPath, skinZipPath, parentHwnd, isLongTerm, tcs, CancellationToken.None);
        var result = tcs.Task.GetAwaiter().GetResult();

        if (result.Success)
        {
            var ticket = result.Ticket;
            if (!string.IsNullOrWhiteSpace(ticket))
            {
                var ticketBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ticket.Trim()));
                Console.WriteLine($"{TicketMarker}{ticketBase64}");
            }
            return 0;
        }

        Console.Error.WriteLine(result.Message ?? "登录未成功或未获取到凭据");
        return 1;
    }

    private static Task<FeverLoginResult> RunWorkerInThreadAsync(nint parentHwnd, bool isLongTerm, CancellationToken ct)
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
                RunMpayLoginThread(mpayDllPath, skinZipPath, parentHwnd, isLongTerm, tcs, ct);
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
        bool isLongTerm,
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
        IntPtr dummyHostHwnd = IntPtr.Zero;
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

            // Ensure a valid parent HWND is passed to mpay_option_parent_hwnd.
            // If parentHwnd is 0, create a minimal 1x1 host window so MPay never fails with "Find main window fail".
            IntPtr effectiveParentHwnd = parentHwnd;
            if (effectiveParentHwnd == IntPtr.Zero)
            {
                dummyHostHwnd = CreateHostWindow();
                effectiveParentHwnd = dummyHostHwnd;
            }

            if (effectiveParentHwnd != IntPtr.Zero && pSetOption != IntPtr.Zero)
            {
                var setOptionFn = Marshal.GetDelegateForFunctionPointer<SetOptionDelegate>(pSetOption);
                var optionNamePtr = Marshal.StringToHGlobalAnsi("mpay_option_parent_hwnd");
                var hwndValPtr = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(hwndValPtr, effectiveParentHwnd);
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

                // `extra` is an opaque native callback argument. It is not guaranteed
                // to point to a NUL-terminated string (or even readable memory).
                if (code == 0)
                {
                    loginSuccess = true;
                    if (pGetTicket != IntPtr.Zero)
                    {
                        var getTicketFn = Marshal.GetDelegateForFunctionPointer<GetUserTicketDelegate>(pGetTicket);
                        getTicketFn(instance);
                    }
                    else
                    {
                        tcs.TrySetResult(new FeverLoginResult(false, null, "登录完成但未取得 Ticket。"));
                        PostQuitMessage(0);
                    }
                }
                else
                {
                    tcs.TrySetResult(new FeverLoginResult(false, null, $"登录完成回调返回状态码: code={code}"));
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

            // Select GameId:
            // Long-term Fever platform login uses "aecglf6ee4aaaarz-g-a50"
            // Temporary QR game scan uses "h55"
            var gameIdStr = isLongTerm ? "aecglf6ee4aaaarz-g-a50" : "h55";
            var initFn = Marshal.GetDelegateForFunctionPointer<InitDelegate>(pInit);
            var pGame = Marshal.StringToHGlobalAnsi(gameIdStr);
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

            Debug.WriteLine($"[FeverLoginService] initResult={initResult}, gameId={gameIdStr}");

            // Call login(instance, 1, 0)
            var loginFn = Marshal.GetDelegateForFunctionPointer<LoginDelegate>(pLogin);
            loginFn(instance, 1, 0);

            // Start background window activation monitor
            using var monitorCts = new CancellationTokenSource();
            _ = Task.Run(() => MonitorAndActivateMpayWindows(monitorCts.Token));

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

            monitorCts.Cancel();

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
            if (dummyHostHwnd != IntPtr.Zero)
            {
                try { DestroyWindow(dummyHostHwnd); } catch { }
            }
            FreeLibrary(hModule);
        }
    }
}

