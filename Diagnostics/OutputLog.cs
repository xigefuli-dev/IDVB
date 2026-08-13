using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;

namespace IDVBuff.Diagnostics;

/// <summary>
/// Process-wide plain-text output log. This runs beside the structured map log and
/// captures diagnostic output as it is emitted, using wall-clock timestamps.
/// </summary>
public static class OutputLog
{
    private const int RetainedLogCount = 10;
    private const long MaximumLogBytes = 16L * 1024 * 1024;
    private const int FirstChanceBurstLimit = 20;
    private static readonly TimeSpan FirstChanceWindow = TimeSpan.FromSeconds(1);
    private static readonly object Gate = new();
    private static readonly object FirstChanceGate = new();

    [ThreadStatic]
    private static bool writing;

    private static StreamWriter? writer;
    private static TextWriter? originalOutput;
    private static TextWriter? originalError;
    private static CapturingTextWriter? capturedOutput;
    private static CapturingTextWriter? capturedError;
    private static OutputTraceListener? traceListener;
    private static bool firstChanceExceptionsAttached;
    private static bool logSizeLimitReached;
    private static DateTime firstChanceWindowStartedUtc = DateTime.UtcNow;
    private static int firstChanceCount;
    private static int suppressedFirstChanceCount;

    public static string? CurrentLogPath { get; private set; }

    public static void Initialize(
        string? logDirectory = null,
        bool captureFirstChanceExceptions = true)
    {
        lock (Gate)
        {
            if (writer is not null)
                return;

            try
            {
                var directory = logDirectory ?? Path.Combine(
                    global::IDVBuff.AppDataPaths.RootDirectory,
                    "Logs");
                Directory.CreateDirectory(directory);
                CleanupOldLogs(directory);

                var timestamp = DateTimeOffset.Now.ToString(
                    "yyyyMMdd-HHmmss-fff",
                    CultureInfo.InvariantCulture);
                var suffix = Guid.NewGuid().ToString("N")[..8];
                CurrentLogPath = Path.Combine(
                    directory,
                    $"output-log-{timestamp}-{suffix}.log");
                writer = new StreamWriter(
                    new FileStream(
                        CurrentLogPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.ReadWrite),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };
                logSizeLimitReached = false;

                originalOutput = Console.Out;
                originalError = Console.Error;
                capturedOutput = new CapturingTextWriter(originalOutput, "STDOUT", "INFO");
                capturedError = new CapturingTextWriter(originalError, "STDERR", "ERROR");
                Console.SetOut(capturedOutput);
                Console.SetError(capturedError);

                traceListener = new OutputTraceListener();
                Trace.Listeners.Add(traceListener);
                if (captureFirstChanceExceptions)
                {
                    AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
                    firstChanceExceptionsAttached = true;
                }
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            }
            catch
            {
                writer?.Dispose();
                writer = null;
                CurrentLogPath = null;
                return;
            }
        }

        Write("INFO", "SYSTEM", "Plain-text output logging started.");
    }

    public static void Write(
        string level,
        string source,
        string message,
        Exception? exception = null)
    {
        if (writer is null || writing)
            return;

        writing = true;
        try
        {
            var normalizedMessage = NormalizeMessage(message);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] "
                + $"[{NormalizeToken(level)}] [{NormalizeToken(source)}] {normalizedMessage}";
            if (exception is not null)
                line += Environment.NewLine + FormatException(exception);

            lock (Gate)
            {
                if (writer is null || logSizeLimitReached)
                    return;
                var encodedLength = writer.Encoding.GetByteCount(line)
                    + writer.Encoding.GetByteCount(Environment.NewLine);
                if (writer.BaseStream.Position + encodedLength >= MaximumLogBytes)
                {
                    writer.WriteLine(
                        $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] "
                        + "[WARNING] [SYSTEM] Output log size limit reached; "
                        + "further messages were suppressed.");
                    logSizeLimitReached = true;
                    return;
                }
                writer.WriteLine(line);
            }
        }
        catch
        {
            // Diagnostics must never affect the application.
        }
        finally
        {
            writing = false;
        }
    }

    public static void Shutdown()
    {
        Write("INFO", "SYSTEM", "Plain-text output logging stopped.");

        lock (Gate)
        {
            if (writer is null)
                return;

            if (firstChanceExceptionsAttached)
            {
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
                firstChanceExceptionsAttached = false;
            }
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

            if (traceListener is not null)
                Trace.Listeners.Remove(traceListener);
            traceListener?.FlushPending();
            traceListener?.Dispose();
            traceListener = null;

            capturedOutput?.FlushPending();
            capturedError?.FlushPending();
            if (ReferenceEquals(Console.Out, capturedOutput) && originalOutput is not null)
                Console.SetOut(originalOutput);
            if (ReferenceEquals(Console.Error, capturedError) && originalError is not null)
                Console.SetError(originalError);
            capturedOutput = null;
            capturedError = null;
            originalOutput = null;
            originalError = null;

            writer.Dispose();
            writer = null;
            logSizeLimitReached = false;
        }
    }

    private static void OnFirstChanceException(
        object? sender,
        FirstChanceExceptionEventArgs args)
    {
        int suppressedCount = 0;
        lock (FirstChanceGate)
        {
            var now = DateTime.UtcNow;
            if (now - firstChanceWindowStartedUtc >= FirstChanceWindow)
            {
                suppressedCount = suppressedFirstChanceCount;
                firstChanceWindowStartedUtc = now;
                firstChanceCount = 0;
                suppressedFirstChanceCount = 0;
            }

            if (firstChanceCount >= FirstChanceBurstLimit)
            {
                suppressedFirstChanceCount++;
                return;
            }
            firstChanceCount++;
        }

        if (suppressedCount > 0)
        {
            Write(
                "WARNING",
                "FIRST-CHANCE",
                $"Suppressed {suppressedCount} repeated first-chance exceptions "
                    + "during the previous one-second window.");
        }

        var exception = args.Exception;
        var includeCallStack = exception is ArgumentException
            or System.Runtime.InteropServices.COMException;
        Write(
            "WARNING",
            "FIRST-CHANCE",
            $"Exception was thrown and may be handled by a try/catch: "
                + $"{exception.GetType().FullName}: {exception.Message}",
            includeCallStack ? exception : null);
    }

    private static void OnUnhandledException(
        object sender,
        System.UnhandledExceptionEventArgs args) =>
        Write(
            "ERROR",
            "UNHANDLED",
            $"Unhandled exception (terminating={args.IsTerminating}).",
            args.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs args) =>
        Write("ERROR", "TASK", "Unobserved task exception.", args.Exception);

    private static void OnProcessExit(object? sender, EventArgs args) => Shutdown();

    private static string NormalizeToken(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "UNKNOWN"
            : value.Trim().Replace(']', ')').ToUpperInvariant();

    private static string NormalizeMessage(string message) =>
        string.IsNullOrEmpty(message)
            ? "(empty message)"
            : message.Replace("\0", "\\0", StringComparison.Ordinal);

    private static string FormatException(Exception exception) =>
        exception.ToString()
            .Replace(Environment.NewLine, Environment.NewLine + "    ", StringComparison.Ordinal)
            .Insert(0, "    ");

    private static void CleanupOldLogs(string directory)
    {
        try
        {
            foreach (var path in Directory.GetFiles(directory, "output-log-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(RetainedLogCount - 1))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Skip files that are still open or cannot be removed.
                }
            }
        }
        catch
        {
            // Cleanup is best effort only.
        }
    }

    private sealed class OutputTraceListener : TraceListener
    {
        private readonly StringBuilder pending = new();
        private readonly object pendingGate = new();

        public override void Write(string? message)
        {
            if (message is null)
                return;
            lock (pendingGate)
                pending.Append(message);
        }

        public override void WriteLine(string? message)
        {
            string complete;
            lock (pendingGate)
            {
                pending.Append(message);
                complete = pending.ToString();
                pending.Clear();
            }
            OutputLog.Write("INFO", "DEBUG/TRACE", complete);
        }

        public void FlushPending()
        {
            string complete;
            lock (pendingGate)
            {
                if (pending.Length == 0)
                    return;
                complete = pending.ToString();
                pending.Clear();
            }
            OutputLog.Write("INFO", "DEBUG/TRACE", complete);
        }
    }

    private sealed class CapturingTextWriter(
        TextWriter original,
        string source,
        string level) : TextWriter
    {
        private readonly StringBuilder pending = new();
        private readonly object pendingGate = new();

        public override Encoding Encoding => original.Encoding;

        public override void Write(char value)
        {
            original.Write(value);
            if (value == '\n')
                EmitPending();
            else if (value != '\r')
            {
                lock (pendingGate)
                    pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            original.Write(value);
            if (value is null)
                return;

            foreach (var character in value)
            {
                if (character == '\n')
                    EmitPending();
                else if (character != '\r')
                {
                    lock (pendingGate)
                        pending.Append(character);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            lock (pendingGate)
                pending.Append(value);
            EmitPending();
        }

        public void FlushPending() => EmitPending();

        private void EmitPending()
        {
            string complete;
            lock (pendingGate)
            {
                if (pending.Length == 0)
                    return;
                complete = pending.ToString();
                pending.Clear();
            }
            OutputLog.Write(level, source, complete);
        }
    }
}
