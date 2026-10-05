using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace IDVBuff.WinUI.RegressionTests;

internal sealed class RegressionTestRunner
{
    private sealed record Result(string Name, double Seconds, Exception? Failure);
    private readonly List<Result> _results = [];
    private readonly string _directory;
    private bool _complete;
    public bool HasFailures => _results.Any(result => result.Failure is not null) || !_complete;

    public RegressionTestRunner(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        WriteResults();
    }

    // Each case owns a fresh panel; all cases run serially on the WinUI dispatcher.
    public async Task<bool> RunAsync(string name, Func<Task> test)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? failure = null;
        try { await test().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (Exception exception) { failure = exception; }
        _results.Add(new(name, stopwatch.Elapsed.TotalSeconds, failure));
        WriteResults();
        // A timed-out case could still own asynchronous work. Do not run another case
        // against the same dispatcher after losing that ownership boundary.
        return !RequiresSuiteStop(failure);
    }

    private static bool RequiresSuiteStop(Exception? failure) =>
        failure is TimeoutException or NativeFixtureCleanupException
        || failure is AggregateException aggregate && aggregate.InnerExceptions.Any(RequiresSuiteStop)
        || failure?.InnerException is { } inner && RequiresSuiteStop(inner);

    public void RecordHostFailure(Exception exception)
    {
        _results.Add(new("WinUIHost", 0, exception));
        WriteResults();
    }

    public void Complete()
    {
        if (_results.Count == 0)
            _results.Add(new("WinUIHost", 0, new InvalidOperationException("The host exited without executing any regression cases.")));
        _complete = true;
        WriteResults();
    }

    private void WriteResults()
    {
        var failures = _results.Count(result => result.Failure is not null);
        var suite = new XElement("testsuite", new XAttribute("name", "IDVB.WinUI.NativeRegression"),
            new XAttribute("tests", _results.Count), new XAttribute("failures", failures),
            new XAttribute("completed", _complete),
            new XAttribute("time", _results.Sum(result => result.Seconds).ToString("F3", CultureInfo.InvariantCulture)),
            _results.Select(result => new XElement("testcase",
                new XAttribute("classname", result.Name.StartsWith("Startup/", StringComparison.Ordinal)
                    ? "IDVBuff.WinUI.RegressionTests.NativeStartupWindowTests"
                    : "IDVBuff.WinUI.RegressionTests.MapControlPanelThemeTests"),
                new XAttribute("name", result.Name),
                new XAttribute("time", result.Seconds.ToString("F3", CultureInfo.InvariantCulture)),
                result.Failure is null ? null : new XElement("failure",
                    new XAttribute("type", result.Failure.GetType().FullName ?? "Exception"),
                    new XAttribute("message", result.Failure.Message), result.Failure.ToString()))));
        new XDocument(suite).Save(Path.Combine(_directory, "results.xml"));
        var header = _complete ? (failures == 0 ? "PASS" : "FAIL") : "RUNNING";
        File.WriteAllText(Path.Combine(_directory, "results.txt"), header + Environment.NewLine
            + $"Cases: {_results.Count}; failures: {failures}" + Environment.NewLine
            + string.Join(Environment.NewLine, _results.Select(result =>
                $"{(result.Failure is null ? "PASS" : "FAIL")} {result.Name} ({result.Seconds:F3}s)"
                + (result.Failure is null ? "" : Environment.NewLine + result.Failure))));
    }
}

internal sealed record NativeRegressionCase(string Name, Func<PanelFixture, Task> Run);
