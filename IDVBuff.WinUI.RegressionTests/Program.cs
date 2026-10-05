using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace IDVBuff.WinUI.RegressionTests;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RegressionTestRunner runner;
        try { runner = new(ResolveResultsDirectory(args)); }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.ExitCode = 2;
            return;
        }
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initializationParameters =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new RegressionApp(runner);
            });
        }
        catch (Exception exception) { runner.RecordHostFailure(exception); }
        finally
        {
            runner.Complete();
            Environment.ExitCode = runner.HasFailures ? 1 : 0;
        }
    }

    private static string ResolveResultsDirectory(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--results-directory"))
            throw new ArgumentException("Usage: IDVBuff.WinUI.RegressionTests.exe [--results-directory <absolute directory>]");
        var configured = args.Length == 2 ? args[1] : Environment.GetEnvironmentVariable("IDVB_REGRESSION_RESULTS");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
                throw new ArgumentException("The results directory must be an absolute path.");
            return Path.GetFullPath(configured);
        }
        return Path.Combine(AppContext.BaseDirectory, "TestResults", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
    }
}
